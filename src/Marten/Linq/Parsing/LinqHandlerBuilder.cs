using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Baseline;
using LamarCodeGeneration;
using Marten.Exceptions;
using Marten.Internal;
using Marten.Internal.Storage;
using Marten.Linq.Fields;
using Marten.Linq.Includes;
using Marten.Linq.Operators;
using Marten.Linq.QueryHandlers;
using Marten.Linq.Selectors;
using Marten.Linq.SqlGeneration;
using Weasel.Postgresql;
using Marten.Util;
using Microsoft.CodeAnalysis.VisualBasic;
using Npgsql;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ResultOperators;

namespace Marten.Linq.Parsing
{
    internal partial class LinqHandlerBuilder
    {
        private readonly IMartenSession _session;


        private readonly MartenLinqQueryProvider _provider;

        private readonly Type _documentType;

        // START HERE -- TRACK WHICH DOCUMENTS ARE INVOLVED
        internal LinqHandlerBuilder(MartenLinqQueryProvider provider, IMartenSession session,
            Expression expression, ResultOperatorBase additionalOperator = null, bool forCompiled = false)
        {
            _session = session;
            _provider = provider;
            Model = forCompiled
                ? MartenQueryParser.TransformQueryFlyweight.GetParsedQuery(expression)
                : MartenQueryParser.Flyweight.GetParsedQuery(expression);

            var orderByClause = Model.BodyClauses
                .OfType<OrderByClause>()
                .FirstOrDefault();
            if (orderByClause != null)
            {
                foreach (var ordering in orderByClause.Orderings)
                {
                    var orderingExpression = ordering.Expression;
                    if (orderingExpression is ConditionalExpression conditionalExpression)
                    {
                        if (conditionalExpression.IfTrue is MemberExpression memberExpressionFromTrue)
                        {
                            ordering.Expression = memberExpressionFromTrue;
                        }
                        else if (conditionalExpression.IfTrue is MemberExpression memberExpressionFromFalse)
                        {
                            ordering.Expression = memberExpressionFromFalse;
                        }
                    }
                }
            }

            if (additionalOperator != null) Model.ResultOperators.Add(additionalOperator);

            _documentType = Model.MainFromClause.ItemType;
            var storage = session.StorageFor(_documentType);
            TopStatement = CurrentStatement = new DocumentStatement(storage);


            if (Model.MainFromClause.FromExpression is SubQueryExpression sub)
            {
                readQueryModel(Model, storage, false, storage.Fields);
                readQueryModel(sub.QueryModel, storage, true, _session.Options.ChildTypeMappingFor(sub.QueryModel.MainFromClause.ItemType));
            }
            else
            {
                readQueryModel(Model, storage, true, storage.Fields);
            }

            wrapIncludes(_provider.AllIncludes);
        }

        public IEnumerable<Type> DocumentTypes()
        {
            yield return _documentType;

            foreach (var plan in _provider.AllIncludes)
            {
                yield return plan.DocumentType;
            }
        }

        private void readQueryModel(QueryModel queryModel, IDocumentStorage storage, bool considerSelectors,
            IFieldMapping fields)
        {
            readBodyClauses(queryModel, storage);


            if (considerSelectors && !(Model.SelectClause.Selector is QuerySourceReferenceExpression))
            {
                var visitor = new SelectorVisitor(this);
                visitor.Visit(Model.SelectClause.Selector);
            }

            foreach (var resultOperator in queryModel.ResultOperators)
            {
                AddResultOperator(resultOperator, fields);
            }


        }

        private void readBodyClauses(QueryModel queryModel, IDocumentStorage storage)
        {
            for (var i = 0; i < queryModel.BodyClauses.Count; i++)
            {
                var clause = queryModel.BodyClauses[i];
                switch (clause)
                {
                    case WhereClause where:
                        CurrentStatement.WhereClauses.Add(@where);
                        break;
                    case OrderByClause orderBy:
                        CurrentStatement.Orderings.AddRange(orderBy.Orderings.Select(x => (x, false)));
                        break;
                    case OrderByComparerClause orderBy:
                        CurrentStatement.Orderings.AddRange(orderBy.Orderings.Select(x => (x, orderBy.CaseInsensitive)));
                        break;
                    case AdditionalFromClause additional:
                        var isComplex = queryModel.BodyClauses.Count > i + 1 || queryModel.ResultOperators.Any() || _provider.AllIncludes.Any();
                        var elementType = additional.ItemType;
                        var collectionField = storage.Fields.FieldFor(additional.FromExpression);

                        CurrentStatement = CurrentStatement.ToSelectMany(collectionField, _session, isComplex, elementType);


                        break;

                    default:
                        throw new NotSupportedException();
                }
            }

        }


        public SelectorStatement CurrentStatement { get; set; }

        public Statement TopStatement { get; private set; }


        public QueryModel Model { get; }

        private void AddResultOperator(ResultOperatorBase resultOperator, IFieldMapping fields)
        {
            switch (resultOperator)
            {
                case TakeResultOperator take:
                    CurrentStatement.Limit = (int)take.Count.Value();
                    break;

                case SkipResultOperator skip:
                    CurrentStatement.Offset = (int)skip.Count.Value();
                    break;

                case AnyResultOperator _:
                    CurrentStatement.ToAny();
                    break;

                case CountResultOperator _:
                    if (CurrentStatement.IsDistinct)
                    {
                        CurrentStatement.ConvertToCommonTableExpression(_session);
                        CurrentStatement = new CountStatement<int>(CurrentStatement);
                    }
                    else
                    {
                        CurrentStatement.ToCount<int>();
                    }
                    break;

                case LongCountResultOperator _:
                    if (CurrentStatement.IsDistinct)
                    {
                        CurrentStatement.ConvertToCommonTableExpression(_session);
                        CurrentStatement = new CountStatement<long>(CurrentStatement);
                    }
                    else
                    {
                        CurrentStatement.ToCount<long>();
                    }
                    break;

                case FirstResultOperator first:
                    CurrentStatement.Limit = 1;
                    CurrentStatement.SingleValue = true;
                    CurrentStatement.ReturnDefaultWhenEmpty = first.ReturnDefaultWhenEmpty;
                    CurrentStatement.CanBeMultiples = true;
                    break;

                case SingleResultOperator single:
                    if (CurrentStatement.Limit == 0)
                    {
                        CurrentStatement.Limit = 2;
                    }

                    CurrentStatement.SingleValue = true;
                    CurrentStatement.ReturnDefaultWhenEmpty = single.ReturnDefaultWhenEmpty;
                    CurrentStatement.CanBeMultiples = false;
                    break;

                case DistinctResultOperator _:
                    CurrentStatement.IsDistinct = true;
                    CurrentStatement.ApplySqlOperator("distinct");
                    break;

                case AverageResultOperator _:
                    CurrentStatement.ApplyAggregateOperator("AVG");
                    break;

                case SumResultOperator _:
                    CurrentStatement.ApplyAggregateOperator("SUM");
                    break;

                case MinResultOperator _:
                    CurrentStatement.ApplyAggregateOperator("MIN");
                    break;

                case MaxResultOperator _:
                    CurrentStatement.ApplyAggregateOperator("MAX");
                    break;

                case LastResultOperator _:
                    throw new InvalidOperationException("Marten does not support Last() or LastOrDefault() queries. Please reverse the ordering and use First()/FirstOrDefault() instead");

                case IncludeResultOperator includeOp:
                    var include = includeOp.BuildInclude(_session, fields);
                    _provider.AllIncludes.Add(include);
                    break;

                default:
                    throw new NotSupportedException("Don't yet know how to deal with " + resultOperator);
            }
        }

        public IQueryHandler<TResult> BuildHandler<TResult>()
        {
            try
            {
                BuildDatabaseStatement();

                var handler = buildHandlerForCurrentStatement<TResult>();

                return _provider.AllIncludes.Any()
                    ? new IncludeQueryHandler<TResult>(handler, _provider.AllIncludes.Select(x => x.BuildReader(_session)).ToArray())
                    : handler;
            }
            catch (NotSupportedException e)
            {
                if (e.Message.StartsWith("Can't infer NpgsqlDbType for type"))
                {
                    throw new BadLinqExpressionException("Marten cannot support custom value types in Linq expression. Please query on either simple properties of the value type, or register a custom IFieldSource for this value type.", e);
                }

                throw;
            }
        }

        public void BuildDatabaseStatement()
        {
            if (_provider.Statistics != null)
            {
                CurrentStatement.UseStatistics(_provider.Statistics);
            }

            var topStatement = TopStatement;
            topStatement.CompileStructure(_session);

            TopStatement = topStatement.Top();
        }

        private void wrapIncludes(IList<IIncludePlan> includes)
        {
            if (!includes.Any()) return;

            // Just need to guarantee that each include has an index
            for (var i = 0; i < includes.Count; i++)
            {
                includes[i].Index = i;
            }

            var statement = new IncludeIdentitySelectorStatement(TopStatement, includes, _session);
            TopStatement = statement.Top();
            CurrentStatement = (SelectorStatement) statement.Current();


        }

        private IQueryHandler<TResult> buildHandlerForCurrentStatement<TResult>()
        {
            if (CurrentStatement.SingleValue)
            {
                return CurrentStatement.BuildSingleResultHandler<TResult>(_session, TopStatement);
            }

            return CurrentStatement.SelectClause.BuildHandler<TResult>(_session, TopStatement, CurrentStatement);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IQueryHandler<TResult> BuildHandler<TDocument, TResult>(ISelector<TDocument> selector,
            Statement statement)
        {
            if (typeof(TResult).CanBeCastTo<IEnumerable<TDocument>>())
            {
                return (IQueryHandler<TResult>)new ListQueryHandler<TDocument>(statement, selector);
            }

            throw new NotSupportedException("Marten does not know how to use result type " + typeof(TResult).FullNameInCode());
        }

        public void BuildDiagnosticCommand(FetchType fetchType, CommandBuilder sql)
        {
            BuildDatabaseStatement();

            switch (fetchType)
            {
                case FetchType.Any:
                    CurrentStatement.ToAny();
                    break;

                case FetchType.Count:
                    CurrentStatement.ToCount<long>();
                    break;

                case FetchType.FetchOne:
                    CurrentStatement.Limit = 1;
                    break;
            }

            TopStatement.Configure(sql);
        }

        public NpgsqlCommand BuildDatabaseCommand(QueryStatistics statistics)
        {
            BuildDatabaseStatement();

            return _session.BuildCommand(TopStatement);
        }
    }
}
