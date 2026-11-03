using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Homeostat.Analyzers;

/// <summary>Finds common mistakes in Homeostat configuration and healing actions.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HomeostatAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> SelectingMethods =
        ImmutableHashSet.Create("ForSource", "Where", "WhenAllActive", "WhenAnyActive");

    private static readonly ImmutableHashSet<string> EndpointTypes = ImmutableHashSet.Create(
        "Homeostat.HomeostatEndpointRouteBuilderExtensions",
        "Homeostat.HomeostatDashboardExtensions",
        "Homeostat.HomeostatAlertEndpointExtensions");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.TaskNotAwaited, Descriptors.ConditionWithoutSource, Descriptors.CancellationIgnored, Descriptors.AnonymousEndpoints,
        Descriptors.PlainBackgroundServiceRestart);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var planBuilder = start.Compilation.GetTypeByMetadataName("Homeostat.Healing.HealingPlanBuilder");
            var homeostatBuilder = start.Compilation.GetTypeByMetadataName("Homeostat.HomeostatBuilder");
            var healingAction = start.Compilation.GetTypeByMetadataName("Homeostat.Healing.IHealingAction");
            if (planBuilder is null || homeostatBuilder is null)
            {
                return; // Homeostat is not referenced.
            }

            var types = new KnownTypes(
                start.Compilation.GetTypeByMetadataName("System.IServiceProvider"),
                start.Compilation.GetTypeByMetadataName("System.Threading.CancellationToken"),
                start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task"),
                start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"),
                start.Compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1"),
                start.Compilation.GetTypeByMetadataName("Microsoft.Extensions.Hosting.BackgroundService"),
                start.Compilation.GetTypeByMetadataName("Homeostat.RestartableBackgroundService"));

            start.RegisterOperationAction(c => AnalyzeInvocation(c, planBuilder, homeostatBuilder, types), OperationKind.Invocation);
            if (healingAction is not null)
            {
                start.RegisterSyntaxNodeAction(c => AnalyzeMethod(c, healingAction, types), SyntaxKind.MethodDeclaration);
            }
        });
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol planBuilder, INamedTypeSymbol homeostatBuilder, KnownTypes types)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, planBuilder)
            && method.Name == "RestartHostedService" && method.TypeArguments.Length == 1
            && types.IsPlainBackgroundService(method.TypeArguments[0]))
        {
            var location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
                ? member.Name.GetLocation()
                : invocation.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.PlainBackgroundServiceRestart, location, method.TypeArguments[0].Name));
            return;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, planBuilder)
            && method.Name is "With" or "Then" or "UndoWith")
        {
            foreach (var argument in invocation.Arguments)
            {
                if (Lambda(argument.Value) is not { } lambda || argument.Parameter?.Type is not INamedTypeSymbol delegateType)
                {
                    continue;
                }

                if (IsAction(delegateType, types.ServiceProvider)
                    && lambda.Syntax is LambdaExpressionSyntax { ExpressionBody: { } body }
                    && context.Operation.SemanticModel?.GetTypeInfo(body, context.CancellationToken).Type is { } returned
                    && types.IsTaskLike(returned))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Descriptors.TaskNotAwaited, body.GetLocation(), returned.Name));
                }

                if (delegateType.DelegateInvokeMethod is { } invoke && invoke.Parameters.Length == 2
                    && SymbolEqualityComparer.Default.Equals(invoke.Parameters[1].Type, types.CancellationToken))
                {
                    CheckCancellation(context.ReportDiagnostic, lambda.Symbol.Parameters[1], lambda.Syntax, context.Operation.SemanticModel, context.CancellationToken);
                }
            }

            return;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, homeostatBuilder) && method.Name == "AddCondition"
            && invocation.Arguments.Length == 2 && Lambda(invocation.Arguments[1].Value) is { } configure)
        {
            var calls = configure.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression is MemberAccessExpressionSyntax m ? m.Name.Identifier.ValueText : null);

            // The builder handed to a helper method (c => ConfigureFeed(c)) is configured where this rule cannot see.
            var parameter = configure.Symbol.Parameters.FirstOrDefault()?.Name;
            var delegated = parameter is not null && configure.Syntax.DescendantNodes().OfType<ArgumentSyntax>()
                .Any(a => a.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == parameter);

            if (!delegated && !calls.Any(name => name is not null && SelectingMethods.Contains(name)))
            {
                var name = invocation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: string text } ? text : "(unnamed)";
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ConditionWithoutSource, invocation.Arguments[0].Syntax.GetLocation(), name));
            }

            return;
        }

        if (method.ContainingType is { } container && EndpointTypes.Contains(container.ToDisplayString()))
        {
            var anonymous = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "allowAnonymous");
            if (anonymous is { ArgumentKind: ArgumentKind.Explicit, Value.ConstantValue: { HasValue: true, Value: true } }
                && !InsideDevelopmentCheck(invocation.Syntax))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.AnonymousEndpoints, anonymous.Syntax.GetLocation(), method.Name));
            }
        }
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, INamedTypeSymbol healingAction, KnownTypes types)
    {
        var declaration = (MethodDeclarationSyntax)context.Node;
        if (declaration.Identifier.ValueText != "ExecuteAsync" || declaration.ParameterList.Parameters.Count != 2
            || context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken) is not { } method
            || !method.ContainingType.AllInterfaces.Contains(healingAction, SymbolEqualityComparer.Default)
            || !SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, types.CancellationToken))
        {
            return;
        }

        CheckCancellation(context.ReportDiagnostic, method.Parameters[1], declaration, context.SemanticModel, context.CancellationToken);
    }

    /// <summary>Reports a token that is never used by code that awaits something.</summary>
    private static void CheckCancellation(Action<Diagnostic> report, IParameterSymbol token, SyntaxNode node, SemanticModel? model, CancellationToken cancellationToken)
    {
        if (model is null || token.Name.StartsWith("_", StringComparison.Ordinal) || token.IsDiscard)
        {
            return;
        }

        var body = node switch
        {
            AnonymousFunctionExpressionSyntax f => (SyntaxNode?)f.Body,
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            _ => null,
        };
        // Asynchronous work: an await, or an expression body that returns a task (x => client.RenewAsync()).
        var asynchronous = body is not null
            && (body.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any()
                || (body is ExpressionSyntax expression && body.Parent is not BlockSyntax
                    && model.GetTypeInfo(expression, cancellationToken).Type is { Name: "Task" or "ValueTask" }));
        if (body is null || !asynchronous)
        {
            return;
        }

        var used = body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.ValueText == token.Name
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, cancellationToken).Symbol, token));
        if (!used && token.Locations.FirstOrDefault() is { } location)
        {
            report(Diagnostic.Create(Descriptors.CancellationIgnored, location, token.Name));
        }
    }

    private static IAnonymousFunctionOperation? Lambda(IOperation value) => value switch
    {
        IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } => lambda,
        IConversionOperation conversion => Lambda(conversion.Operand),
        IAnonymousFunctionOperation lambda => lambda,
        _ => null,
    };

    private static bool IsAction(INamedTypeSymbol type, INamedTypeSymbol? serviceProvider)
        => type is { Name: "Action", Arity: 1, ContainingNamespace.Name: "System" }
            && SymbolEqualityComparer.Default.Equals(type.TypeArguments[0], serviceProvider);

    /// <summary>
    /// Whether the node is in the "then" branch of <c>if (x.IsDevelopment())</c>: not the else branch, not a negated or
    /// combined condition.
    /// </summary>
    private static bool InsideDevelopmentCheck(SyntaxNode node)
    {
        for (SyntaxNode? child = node, parent = node.Parent; parent is not null; child = parent, parent = parent.Parent)
        {
            if (parent is IfStatementSyntax ifStatement && ReferenceEquals(ifStatement.Statement, child)
                && ifStatement.Condition is InvocationExpressionSyntax invocation
                && invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => null,
                } == "IsDevelopment")
            {
                return true;
            }
        }

        return false;
    }

    private sealed class KnownTypes(
        INamedTypeSymbol? serviceProvider,
        INamedTypeSymbol? cancellationToken,
        INamedTypeSymbol? task,
        INamedTypeSymbol? valueTask,
        INamedTypeSymbol? valueTaskOfT,
        INamedTypeSymbol? backgroundService,
        INamedTypeSymbol? restartable)
    {
        public INamedTypeSymbol? ServiceProvider { get; } = serviceProvider;

        public INamedTypeSymbol? CancellationToken { get; } = cancellationToken;

        public bool IsPlainBackgroundService(ITypeSymbol type)
        {
            if (backgroundService is null)
            {
                return false;
            }

            var isBackground = false;
            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (restartable is not null && SymbolEqualityComparer.Default.Equals(current, restartable))
                {
                    return false;
                }

                isBackground |= SymbolEqualityComparer.Default.Equals(current, backgroundService);
            }

            return isBackground;
        }

        public bool IsTaskLike(ITypeSymbol type)
        {
            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, task)
                    || SymbolEqualityComparer.Default.Equals(current, valueTask)
                    || SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, valueTaskOfT))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
