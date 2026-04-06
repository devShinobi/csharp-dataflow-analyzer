using System.Collections.Generic;
using System.Linq;
using Xunit;
using CSharpDataFlowAnalyzer;
using CSharpDataFlowAnalyzer.Analysis;

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

    public static PropertyNode Property(string id, string name, string type,
        bool hasSetter = true) => new()
    {
        Id = id, Name = name, Type = type, HasGetter = true, HasSetter = hasSetter
    };

    public static ParamNode Param(string name, string type) => new()
    {
        Id = $"ctor/param:{name}", Name = name, Type = type
    };

    public static CallNode Call(string methodName, string? resolvedMethodId = null) => new()
    {
        Id = $"call:{methodName}[0]", MethodName = methodName, Expression = methodName,
        ResolvedMethodId = resolvedMethodId
    };

    public static AnalysisResult Result(string source, params ClassUnit[] units)
    {
        var fg = new FlowGraph { Source = source };
        fg.Units.AddRange(units);
        return new AnalysisResult { Source = source, FlowGraph = fg };
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// DependencyAnalyzer tests
// ═══════════════════════════════════════════════════════════════════════════

public class DependencyAnalyzerTests
{
    [Fact]
    public void Analyze_ConstructorParam_CreatesDependencyEdge()
    {
        var service = OnboardingFixture.Class("MyApp.OrderService", "OrderService", ns: "MyApp");
        var ctor = OnboardingFixture.Method("MyApp.OrderService::ctor[0]", ".ctor");
        ctor.Params.Add(OnboardingFixture.Param("repo", "IOrderRepository"));
        service.Constructors.Add(ctor);

        var repo = OnboardingFixture.Class("MyApp.IOrderRepository", "IOrderRepository",
            kind: "interface", ns: "MyApp");

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Service.cs", service, repo)
        };

        var (graph, _) = DependencyAnalyzer.Analyze(results);

        Assert.Contains(graph.ClassDependencies, e =>
            e.From == "MyApp.OrderService" && e.To == "MyApp.IOrderRepository"
            && e.Kind == "constructor-param");
    }

    [Fact]
    public void Analyze_Inheritance_CreatesDependencyEdge()
    {
        var derived = OnboardingFixture.Class("MyApp.Dog", "Dog", ns: "MyApp",
            baseTypes: new List<string> { "MyApp.Animal" });
        var baseClass = OnboardingFixture.Class("MyApp.Animal", "Animal", ns: "MyApp");

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Animals.cs", derived, baseClass)
        };

        var (graph, relationships) = DependencyAnalyzer.Analyze(results);

        Assert.Contains(graph.ClassDependencies, e =>
            e.From == "MyApp.Dog" && e.To == "MyApp.Animal" && e.Kind == "inheritance");

        var dogRel = relationships.First(r => r.ClassId == "MyApp.Dog");
        Assert.Equal("MyApp.Animal", dogRel.BaseClass);
    }

    [Fact]
    public void Analyze_InterfaceImpl_SeparatedFromBaseClass()
    {
        var cls = OnboardingFixture.Class("MyApp.Repo", "Repo", ns: "MyApp",
            baseTypes: new List<string> { "MyApp.IRepository" });

        var iface = OnboardingFixture.Class("MyApp.IRepository", "IRepository",
            kind: "interface", ns: "MyApp");

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Repo.cs", cls, iface)
        };

        var (_, relationships) = DependencyAnalyzer.Analyze(results);

        var repoRel = relationships.First(r => r.ClassId == "MyApp.Repo");
        Assert.Null(repoRel.BaseClass); // IRepository is an interface, not a base class
        Assert.Contains("MyApp.IRepository", repoRel.ImplementedInterfaces);
    }

    [Fact]
    public void Analyze_FanInFanOut_Computed()
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

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("App.cs", service, repo, consumer)
        };

        var (_, relationships) = DependencyAnalyzer.Analyze(results);

        var serviceRel = relationships.First(r => r.ClassId == "App.Service");
        Assert.True(serviceRel.FanOut >= 1, "Service depends on Repo → fanOut >= 1");
        Assert.True(serviceRel.FanIn >= 1, "Consumer depends on Service → fanIn >= 1");
    }

    [Fact]
    public void Analyze_FieldType_CreatesDependencyEdge()
    {
        var service = OnboardingFixture.Class("App.Service", "Service", ns: "App");
        service.Fields.Add(OnboardingFixture.Field("App.Service::field:_repo", "_repo", "Repo"));

        var repo = OnboardingFixture.Class("App.Repo", "Repo", ns: "App");

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("App.cs", service, repo)
        };

        var (graph, _) = DependencyAnalyzer.Analyze(results);

        Assert.Contains(graph.ClassDependencies, e =>
            e.From == "App.Service" && e.To == "App.Repo" && e.Kind == "field-type");
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// EntryPointDetector tests
// ═══════════════════════════════════════════════════════════════════════════

public class EntryPointDetectorTests
{
    [Fact]
    public void Detect_MainMethod_ReturnsMainEntryPoint()
    {
        var program = OnboardingFixture.Class("Program", "Program");
        program.Methods.Add(OnboardingFixture.Method("Program::Main[0]", "Main",
            isStatic: true));

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Program.cs", program)
        };

        var entryPoints = EntryPointDetector.Detect(results);

        Assert.Single(entryPoints);
        Assert.Equal("main", entryPoints[0].Kind);
        Assert.Equal("Program", entryPoints[0].ClassId);
    }

    [Fact]
    public void Detect_TopLevelStatements_ReturnsSyntheticMain()
    {
        var program = OnboardingFixture.Class("Program", "Program");
        program.Methods.Add(OnboardingFixture.Method("Program::<Main>$[0]", "<Main>$",
            isStatic: true));

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Program.cs", program)
        };

        var entryPoints = EntryPointDetector.Detect(results);

        Assert.Single(entryPoints);
        Assert.Equal("main", entryPoints[0].Kind);
    }

    [Fact]
    public void Detect_ControllerWithActions_ReturnsControllerActions()
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

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("OrderController.cs", controller)
        };

        var entryPoints = EntryPointDetector.Detect(results);

        Assert.Equal(2, entryPoints.Count);
        Assert.All(entryPoints, ep => Assert.Equal("controller-action", ep.Kind));
        Assert.Contains(entryPoints, ep => ep.HttpMethod == "GET");
        Assert.Contains(entryPoints, ep => ep.HttpMethod == "POST");
    }

    [Fact]
    public void Detect_BackgroundService_ReturnsBackgroundServiceEntryPoint()
    {
        var service = OnboardingFixture.Class("Workers.Processor", "Processor",
            ns: "Workers",
            baseTypes: new List<string> { "Microsoft.Extensions.Hosting.BackgroundService" });

        service.Methods.Add(OnboardingFixture.Method(
            "Workers.Processor::ExecuteAsync[0]", "ExecuteAsync",
            isAsync: true, returnType: "Task"));

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Processor.cs", service)
        };

        var entryPoints = EntryPointDetector.Detect(results);

        Assert.Single(entryPoints);
        Assert.Equal("background-service", entryPoints[0].Kind);
        Assert.Equal("Workers.Processor::ExecuteAsync[0]", entryPoints[0].MethodId);
    }

    [Fact]
    public void Detect_MediatRHandler_ReturnsHandler()
    {
        var handler = OnboardingFixture.Class("Handlers.CreateOrder", "CreateOrder",
            ns: "Handlers",
            baseTypes: new List<string> { "MediatR.IRequestHandler<CreateOrderCommand, OrderResult>" });

        handler.Methods.Add(OnboardingFixture.Method(
            "Handlers.CreateOrder::Handle[0]", "Handle",
            isAsync: true));

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("CreateOrder.cs", handler)
        };

        var entryPoints = EntryPointDetector.Detect(results);

        Assert.Single(entryPoints);
        Assert.Equal("mediatr-handler", entryPoints[0].Kind);
    }

    [Fact]
    public void Detect_NoEntryPoints_ReturnsEmptyList()
    {
        var dto = OnboardingFixture.Class("Models.OrderDto", "OrderDto",
            kind: "record", ns: "Models");

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("OrderDto.cs", dto)
        };

        var entryPoints = EntryPointDetector.Detect(results);

        Assert.Empty(entryPoints);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// HotPathDetector tests
// ═══════════════════════════════════════════════════════════════════════════

public class HotPathDetectorTests
{
    [Fact]
    public void Detect_RankedByTotalConnections()
    {
        var relationships = new List<ClassRelationship>
        {
            new() { ClassId = "A", ClassName = "A", FanIn = 5, FanOut = 3 },
            new() { ClassId = "B", ClassName = "B", FanIn = 1, FanOut = 1 },
            new() { ClassId = "C", ClassName = "C", FanIn = 3, FanOut = 4 },
        };

        var hotNodes = HotPathDetector.Detect(relationships, topN: 3);

        Assert.Equal(3, hotNodes.Count);
        Assert.Equal("A", hotNodes[0].ClassId); // 5+3 = 8 total
        Assert.Equal(1, hotNodes[0].Rank);
        Assert.Equal("C", hotNodes[1].ClassId); // 3+4 = 7 total
        Assert.Equal(2, hotNodes[1].Rank);
        Assert.Equal("B", hotNodes[2].ClassId); // 1+1 = 2 total
        Assert.Equal(3, hotNodes[2].Rank);
    }

    [Fact]
    public void Detect_RoleClassification_Hub()
    {
        var relationships = new List<ClassRelationship>
        {
            new() { ClassId = "Hub", ClassName = "Hub", FanIn = 10, FanOut = 10 },
            new() { ClassId = "Leaf1", ClassName = "Leaf1", FanIn = 0, FanOut = 0 },
            new() { ClassId = "Leaf2", ClassName = "Leaf2", FanIn = 0, FanOut = 0 },
        };

        var hotNodes = HotPathDetector.Detect(relationships, topN: 3);

        Assert.Equal("hub", hotNodes[0].Role);
    }

    [Fact]
    public void Detect_TopN_LimitsOutput()
    {
        var relationships = Enumerable.Range(0, 20)
            .Select(i => new ClassRelationship
            {
                ClassId = $"Class{i}", ClassName = $"Class{i}",
                FanIn = 20 - i, FanOut = i
            })
            .ToList();

        var hotNodes = HotPathDetector.Detect(relationships, topN: 5);

        Assert.Equal(5, hotNodes.Count);
    }

    [Fact]
    public void Detect_EmptyInput_ReturnsEmpty()
    {
        var hotNodes = HotPathDetector.Detect(new List<ClassRelationship>());
        Assert.Empty(hotNodes);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// ClassExplainer tests
// ═══════════════════════════════════════════════════════════════════════════

public class ClassExplainerTests
{
    [Fact]
    public void Explain_InfersStatelessUtility()
    {
        var utility = OnboardingFixture.Class("Utils.Helpers", "Helpers", ns: "Utils");
        utility.Methods.Add(OnboardingFixture.Method("Utils.Helpers::Format[0]", "Format",
            isStatic: true, returnType: "string"));

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Helpers.cs", utility)
        };

        var explanation = ClassExplainer.Explain("Utils.Helpers", results,
            new List<ClassRelationship>
            {
                new() { ClassId = "Utils.Helpers", ClassName = "Helpers", Kind = "class" }
            },
            new List<EntryPoint>(), new List<HotNode>());

        Assert.NotNull(explanation);
        Assert.Contains("Stateless utility/helper class", explanation!.Responsibilities);
    }

    [Fact]
    public void Explain_InfersMutableState()
    {
        var entity = OnboardingFixture.Class("Models.Order", "Order", ns: "Models");
        entity.Fields.Add(OnboardingFixture.Field("Models.Order::field:_items", "_items", "List<OrderItem>"));

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Order.cs", entity)
        };

        var explanation = ClassExplainer.Explain("Models.Order", results,
            new List<ClassRelationship>
            {
                new() { ClassId = "Models.Order", ClassName = "Order", Kind = "class" }
            },
            new List<EntryPoint>(), new List<HotNode>());

        Assert.NotNull(explanation);
        Assert.Contains(explanation!.Responsibilities, r => r.Contains("mutable state"));
        Assert.True(explanation.State.HasMutableState);
    }

    [Fact]
    public void Explain_PartialMatchByClassName()
    {
        var cls = OnboardingFixture.Class("Deep.Namespace.OrderService", "OrderService",
            ns: "Deep.Namespace");

        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("OrderService.cs", cls)
        };

        // Look up by short name
        var explanation = ClassExplainer.Explain("OrderService", results,
            new List<ClassRelationship>(), new List<EntryPoint>(), new List<HotNode>());

        Assert.NotNull(explanation);
        Assert.Equal("OrderService", explanation!.ClassName);
    }

    [Fact]
    public void Explain_UnknownClass_ReturnsNull()
    {
        var results = new List<AnalysisResult>
        {
            OnboardingFixture.Result("Empty.cs")
        };

        var explanation = ClassExplainer.Explain("NonExistent", results,
            new List<ClassRelationship>(), new List<EntryPoint>(), new List<HotNode>());

        Assert.Null(explanation);
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
// HtmlReportGenerator tests
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
