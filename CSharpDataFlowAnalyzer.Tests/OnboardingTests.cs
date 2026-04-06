using System.Collections.Generic;
using System.Linq;
using Xunit;
using CSharpDataFlowAnalyzer;

namespace CSharpDataFlowAnalyzer.Tests;

// Helpers for building minimal onboarding fixtures.
file static class OnboardingFixture
{
    public static ClassUnit Class(string id, string name, string kind = "class",
        string? ns = null, List<string>? baseTypes = null, List<string>? attributes = null)
    {
        var unit = new ClassUnit { Id = id, Name = name, Kind = kind, Namespace = ns };
        if (baseTypes != null) unit.BaseTypes.AddRange(baseTypes);
        if (attributes != null) unit.Attributes = attributes;
        return unit;
    }

    public static MethodNode Method(string id, string name, string returnType = "void",
        string accessibility = "public", bool isAsync = false, bool isStatic = false,
        List<string>? attributes = null)
    {
        var m = new MethodNode
        {
            Id = id, Name = name, ReturnType = returnType,
            Accessibility = accessibility, IsAsync = isAsync, IsStatic = isStatic
        };
        if (attributes != null) m.Attributes = attributes;
        return m;
    }

    public static FieldNode Field(string id, string name, string type,
        bool isReadonly = false, bool isStatic = false) => new()
    {
        Id = id, Name = name, Type = type, IsReadonly = isReadonly, IsStatic = isStatic
    };

    public static ParamNode Param(string name, string type) => new()
    {
        Id = $"ctor/param:{name}", Name = name, Type = type
    };

    public static AnalysisResult Result(string source, params ClassUnit[] units)
    {
        var fg = new FlowGraph { Source = source };
        fg.Units.AddRange(units);
        return new AnalysisResult { Source = source, FlowGraph = fg };
    }

    /// <summary>Runs BuildOnboarding through the public API.</summary>
    public static OnboardingOutput Onboard(List<AnalysisResult> results, string? explainClassId = null) =>
        AnalyzerEngine.BuildOnboarding(results, explainClassId, log: _ => { });
}

// ═══════════════════════════════════════════════════════════════════════════
// Dependency graph tests (via AnalyzerEngine.BuildOnboarding)
// ═══════════════════════════════════════════════════════════════════════════

public class DependencyGraphTests
{
    [Fact]
    public void ConstructorParam_CreatesDependencyEdge()
    {
        var service = OnboardingFixture.Class("MyApp.OrderService", "OrderService", ns: "MyApp");
        var ctor = OnboardingFixture.Method("MyApp.OrderService::ctor[0]", ".ctor");
        ctor.Params.Add(OnboardingFixture.Param("repo", "IOrderRepository"));
        service.Constructors.Add(ctor);

        var repo = OnboardingFixture.Class("MyApp.IOrderRepository", "IOrderRepository",
            kind: "interface", ns: "MyApp");

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("Service.cs", service, repo)
        });

        Assert.Contains(output.DependencyGraph.ClassDependencies, e =>
            e.From == "MyApp.OrderService" && e.To == "MyApp.IOrderRepository"
            && e.Kind == "constructor-param");
    }

    [Fact]
    public void Inheritance_CreatesDependencyEdge()
    {
        var derived = OnboardingFixture.Class("MyApp.Dog", "Dog", ns: "MyApp",
            baseTypes: new List<string> { "MyApp.Animal" });
        var baseClass = OnboardingFixture.Class("MyApp.Animal", "Animal", ns: "MyApp");

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("Animals.cs", derived, baseClass)
        });

        Assert.Contains(output.DependencyGraph.ClassDependencies, e =>
            e.From == "MyApp.Dog" && e.To == "MyApp.Animal" && e.Kind == "inheritance");

        var dogRel = output.ClassRelationships.First(r => r.ClassId == "MyApp.Dog");
        Assert.Equal("MyApp.Animal", dogRel.BaseClass);
    }

    [Fact]
    public void InterfaceImpl_SeparatedFromBaseClass()
    {
        var cls = OnboardingFixture.Class("MyApp.Repo", "Repo", ns: "MyApp",
            baseTypes: new List<string> { "MyApp.IRepository" });

        var iface = OnboardingFixture.Class("MyApp.IRepository", "IRepository",
            kind: "interface", ns: "MyApp");

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("Repo.cs", cls, iface)
        });

        var repoRel = output.ClassRelationships.First(r => r.ClassId == "MyApp.Repo");
        Assert.Null(repoRel.BaseClass);
        Assert.Contains("MyApp.IRepository", repoRel.ImplementedInterfaces);
    }

    [Fact]
    public void FanInFanOut_Computed()
    {
        var service = OnboardingFixture.Class("App.Service", "Service", ns: "App");
        var ctor = OnboardingFixture.Method("App.Service::ctor[0]", ".ctor");
        ctor.Params.Add(OnboardingFixture.Param("repo", "Repo"));
        service.Constructors.Add(ctor);

        var repo = OnboardingFixture.Class("App.Repo", "Repo", ns: "App");
        var consumer = OnboardingFixture.Class("App.Consumer", "Consumer", ns: "App");
        var consumerCtor = OnboardingFixture.Method("App.Consumer::ctor[0]", ".ctor");
        consumerCtor.Params.Add(OnboardingFixture.Param("svc", "Service"));
        consumer.Constructors.Add(consumerCtor);

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("App.cs", service, repo, consumer)
        });

        var serviceRel = output.ClassRelationships.First(r => r.ClassId == "App.Service");
        Assert.True(serviceRel.FanOut >= 1, "Service depends on Repo → fanOut >= 1");
        Assert.True(serviceRel.FanIn >= 1, "Consumer depends on Service → fanIn >= 1");
    }

    [Fact]
    public void FieldType_CreatesDependencyEdge()
    {
        var service = OnboardingFixture.Class("App.Service", "Service", ns: "App");
        service.Fields.Add(OnboardingFixture.Field("App.Service::field:_repo", "_repo", "Repo"));

        var repo = OnboardingFixture.Class("App.Repo", "Repo", ns: "App");

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("App.cs", service, repo)
        });

        Assert.Contains(output.DependencyGraph.ClassDependencies, e =>
            e.From == "App.Service" && e.To == "App.Repo" && e.Kind == "field-type");
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Entry point detection tests (via AnalyzerEngine.BuildOnboarding)
// ═══════════════════════════════════════════════════════════════════════════

public class EntryPointTests
{
    [Fact]
    public void MainMethod_Detected()
    {
        var program = OnboardingFixture.Class("Program", "Program");
        program.Methods.Add(OnboardingFixture.Method("Program::Main[0]", "Main",
            isStatic: true));

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("Program.cs", program)
        });

        Assert.Single(output.EntryPoints);
        Assert.Equal("main", output.EntryPoints[0].Kind);
        Assert.Equal("Program", output.EntryPoints[0].ClassId);
    }

    [Fact]
    public void TopLevelStatements_DetectedAsSyntheticMain()
    {
        var program = OnboardingFixture.Class("Program", "Program");
        program.Methods.Add(OnboardingFixture.Method("Program::<Main>$[0]", "<Main>$",
            isStatic: true));

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("Program.cs", program)
        });

        Assert.Single(output.EntryPoints);
        Assert.Equal("main", output.EntryPoints[0].Kind);
    }

    [Fact]
    public void ControllerActions_Detected()
    {
        var controller = OnboardingFixture.Class("Api.OrderController", "OrderController",
            ns: "Api",
            baseTypes: new List<string> { "Microsoft.AspNetCore.Mvc.ControllerBase" });

        controller.Methods.Add(OnboardingFixture.Method(
            "Api.OrderController::GetAll[0]", "GetAll",
            attributes: new List<string> { "Microsoft.AspNetCore.Mvc.HttpGetAttribute" }));

        controller.Methods.Add(OnboardingFixture.Method(
            "Api.OrderController::Create[0]", "Create",
            attributes: new List<string> { "Microsoft.AspNetCore.Mvc.HttpPostAttribute" }));

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("OrderController.cs", controller)
        });

        Assert.Equal(2, output.EntryPoints.Count);
        Assert.All(output.EntryPoints, ep => Assert.Equal("controller-action", ep.Kind));
        Assert.Contains(output.EntryPoints, ep => ep.HttpMethod == "GET");
        Assert.Contains(output.EntryPoints, ep => ep.HttpMethod == "POST");
    }

    [Fact]
    public void BackgroundService_Detected()
    {
        var service = OnboardingFixture.Class("Workers.Processor", "Processor",
            ns: "Workers",
            baseTypes: new List<string> { "Microsoft.Extensions.Hosting.BackgroundService" });

        service.Methods.Add(OnboardingFixture.Method(
            "Workers.Processor::ExecuteAsync[0]", "ExecuteAsync",
            isAsync: true, returnType: "Task"));

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("Processor.cs", service)
        });

        Assert.Single(output.EntryPoints);
        Assert.Equal("background-service", output.EntryPoints[0].Kind);
        Assert.Equal("Workers.Processor::ExecuteAsync[0]", output.EntryPoints[0].MethodId);
    }

    [Fact]
    public void MediatRHandler_Detected()
    {
        var handler = OnboardingFixture.Class("Handlers.CreateOrder", "CreateOrder",
            ns: "Handlers",
            baseTypes: new List<string> { "MediatR.IRequestHandler<CreateOrderCommand, OrderResult>" });

        handler.Methods.Add(OnboardingFixture.Method(
            "Handlers.CreateOrder::Handle[0]", "Handle",
            isAsync: true));

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("CreateOrder.cs", handler)
        });

        Assert.Single(output.EntryPoints);
        Assert.Equal("mediatr-handler", output.EntryPoints[0].Kind);
    }

    [Fact]
    public void NoEntryPoints_ReturnsEmptyList()
    {
        var dto = OnboardingFixture.Class("Models.OrderDto", "OrderDto",
            kind: "record", ns: "Models");

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("OrderDto.cs", dto)
        });

        Assert.Empty(output.EntryPoints);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Hot node tests (uses public model types directly — no internals needed)
// ═══════════════════════════════════════════════════════════════════════════

public class HotNodeTests
{
    [Fact]
    public void HotNodes_RankedByTotalConnections()
    {
        // Build a graph where Service has the most connections
        var service = OnboardingFixture.Class("App.Service", "Service", ns: "App");
        var serviceCtor = OnboardingFixture.Method("App.Service::ctor[0]", ".ctor");
        serviceCtor.Params.Add(OnboardingFixture.Param("repoA", "RepoA"));
        serviceCtor.Params.Add(OnboardingFixture.Param("repoB", "RepoB"));
        service.Constructors.Add(serviceCtor);

        var repoA = OnboardingFixture.Class("App.RepoA", "RepoA", ns: "App");
        var repoB = OnboardingFixture.Class("App.RepoB", "RepoB", ns: "App");

        var consumer = OnboardingFixture.Class("App.Consumer", "Consumer", ns: "App");
        var consumerCtor = OnboardingFixture.Method("App.Consumer::ctor[0]", ".ctor");
        consumerCtor.Params.Add(OnboardingFixture.Param("svc", "Service"));
        consumer.Constructors.Add(consumerCtor);

        var output = OnboardingFixture.Onboard(new List<AnalysisResult>
        {
            OnboardingFixture.Result("App.cs", service, repoA, repoB, consumer)
        });

        Assert.NotEmpty(output.HotNodes);
        // Service should rank highest: fanOut=2 (RepoA, RepoB) + fanIn=1 (Consumer)
        Assert.Equal("App.Service", output.HotNodes[0].ClassId);
        Assert.Equal(1, output.HotNodes[0].Rank);
    }

    [Fact]
    public void HotNodes_EmptyInput_ReturnsEmpty()
    {
        var output = OnboardingFixture.Onboard(new List<AnalysisResult>());
        Assert.Empty(output.HotNodes);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Class explanation tests (via --explain through BuildOnboarding)
// ═══════════════════════════════════════════════════════════════════════════

public class ClassExplanationTests
{
    [Fact]
    public void Explain_InfersStatelessUtility()
    {
        var utility = OnboardingFixture.Class("Utils.Helpers", "Helpers", ns: "Utils");
        utility.Methods.Add(OnboardingFixture.Method("Utils.Helpers::Format[0]", "Format",
            isStatic: true, returnType: "string"));

        var output = OnboardingFixture.Onboard(
            new List<AnalysisResult> { OnboardingFixture.Result("Helpers.cs", utility) },
            explainClassId: "Utils.Helpers");

        Assert.NotNull(output.ClassExplanation);
        Assert.Contains("Stateless utility/helper class", output.ClassExplanation!.Responsibilities);
    }

    [Fact]
    public void Explain_InfersMutableState()
    {
        var entity = OnboardingFixture.Class("Models.Order", "Order", ns: "Models");
        entity.Fields.Add(OnboardingFixture.Field("Models.Order::field:_items", "_items", "List<OrderItem>"));

        var output = OnboardingFixture.Onboard(
            new List<AnalysisResult> { OnboardingFixture.Result("Order.cs", entity) },
            explainClassId: "Models.Order");

        Assert.NotNull(output.ClassExplanation);
        Assert.Contains(output.ClassExplanation!.Responsibilities, r => r.Contains("mutable state"));
        Assert.True(output.ClassExplanation.State.HasMutableState);
    }

    [Fact]
    public void Explain_PartialMatchByClassName()
    {
        var cls = OnboardingFixture.Class("Deep.Namespace.OrderService", "OrderService",
            ns: "Deep.Namespace");

        var output = OnboardingFixture.Onboard(
            new List<AnalysisResult> { OnboardingFixture.Result("OrderService.cs", cls) },
            explainClassId: "OrderService");

        Assert.NotNull(output.ClassExplanation);
        Assert.Equal("OrderService", output.ClassExplanation!.ClassName);
    }

    [Fact]
    public void Explain_UnknownClass_ReturnsNull()
    {
        var output = OnboardingFixture.Onboard(
            new List<AnalysisResult> { OnboardingFixture.Result("Empty.cs") },
            explainClassId: "NonExistent");

        Assert.Null(output.ClassExplanation);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// ParsedArgs onboarding flag tests
// ═══════════════════════════════════════════════════════════════════════════

public class ParsedArgsOnboardingTests
{
    [Fact]
    public void Parse_OnboardFlag_SetsOnboard()
    {
        var args = ParsedArgs.Parse(new[] { "file.cs", "--onboard" });
        Assert.True(args.Onboard);
        Assert.Equal("json", args.Format);
    }

    [Fact]
    public void Parse_ExplainFlag_SetsClassIdAndImpliesOnboard()
    {
        var args = ParsedArgs.Parse(new[] { "file.cs", "--explain", "MyClass" });
        Assert.True(args.Onboard);
        Assert.Equal("MyClass", args.ExplainClassId);
    }

    [Fact]
    public void Parse_FormatHtml_SetsFormat()
    {
        var args = ParsedArgs.Parse(new[] { "file.cs", "--onboard", "--format", "html" });
        Assert.Equal("html", args.Format);
    }

    [Fact]
    public void Parse_DefaultFormat_IsJson()
    {
        var args = ParsedArgs.Parse(new[] { "file.cs" });
        Assert.Equal("json", args.Format);
        Assert.False(args.Onboard);
        Assert.Null(args.ExplainClassId);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// HtmlReportGenerator tests (uses public API only)
// ═══════════════════════════════════════════════════════════════════════════

public class HtmlReportGeneratorTests
{
    [Fact]
    public void Generate_ProducesValidHtml()
    {
        var output = new OnboardingOutput
        {
            EntryPoints = new List<EntryPoint>
            {
                new() { ClassId = "Program", MethodId = "Program::Main[0]", Kind = "main" }
            },
            HotNodes = new List<HotNode>
            {
                new() { ClassId = "App.Service", ClassName = "Service", FanIn = 5, FanOut = 3,
                         TotalConnections = 8, Rank = 1, Role = "hub" }
            },
            ClassRelationships = new List<ClassRelationship>
            {
                new() { ClassId = "App.Service", ClassName = "Service", Namespace = "App", Kind = "class" }
            }
        };

        var html = HtmlReportGenerator.Generate(output);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Codebase Onboarding Report", html);
        Assert.Contains("App.Service", html);
        Assert.Contains("Entry Points", html);
        Assert.Contains("Hot Nodes", html);
    }
}
