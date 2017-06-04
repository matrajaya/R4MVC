using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Options;
using R4Mvc.Tools.Extensions;
using R4Mvc.Tools.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace R4Mvc.Tools
{
    public class R4MvcGenerator
    {
        private readonly IControllerRewriterService _controllerRewriter;

        private readonly IControllerGeneratorService _controllerGenerator;

        private readonly IStaticFileGeneratorService _staticFileGenerator;

        private readonly IFilePersistService _filePersistService;

        private readonly Settings _settings;

        private static readonly string[] pragmaCodes = { "1591", "3008", "3009", "0108" };

        public const string R4MvcFileName = "R4Mvc.generated.cs";

        private const string _headerText = 
@"// <auto-generated />
// This file was generated by a R4Mvc.
// Don't change it directly as your change would get overwritten.  Instead, make changes
// to the r4mvc.json file (i.e. the settings file), save it and rebuild.

// Make sure the compiler doesn't complain about missing Xml comments and CLS compliance
// 0108: suppress ""Foo hides inherited member Foo.Use the new keyword if hiding was intended."" when a controller and its abstract parent are both processed";

        public R4MvcGenerator(
            IControllerRewriterService controllerRewriter,
            IControllerGeneratorService controllerGenerator,
            IStaticFileGeneratorService staticFileGenerator,
            IFilePersistService filePersistService,
            IOptions<Settings> settings)
        {
            _controllerRewriter = controllerRewriter;
            _controllerGenerator = controllerGenerator;
            _staticFileGenerator = staticFileGenerator;
            _filePersistService = filePersistService;
            _settings = settings.Value;
        }


        public void Generate(CSharpCompilation compilation, string projectRoot)
        {
            // create static MVC class and add controller fields 
            var mvcStaticClass =
                SyntaxNodeHelpers.CreateClass(_settings.HelpersPrefix, null, SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword)
                    .WithAttributes(SyntaxNodeHelpers.CreateGeneratedCodeAttribute(), SyntaxNodeHelpers.CreateDebugNonUserCodeAttribute());

            var controllers = _controllerRewriter.RewriteControllers(compilation, R4MvcFileName);
            var namespaceGroups = controllers.GroupBy(c => c.Ancestors().OfType<NamespaceDeclarationSyntax>().First().Name.ToFullString().Trim());
            var generatedControllers = new List<NamespaceDeclarationSyntax>();
            foreach (var nameGroup in namespaceGroups)
            {
                var namespaceNode = SyntaxNodeHelpers.CreateNamespace(nameGroup.Key);
                var areaMatch = Regex.Match(nameGroup.Key, ".Areas.(\\w+).Controllers");
                var areaName = areaMatch.Success ? areaMatch.Groups[1].Value : string.Empty;

                foreach (var controllerNode in nameGroup)
                {
                    var model = compilation.GetSemanticModel(controllerNode.SyntaxTree);
                    var controllerSymbol = model.GetDeclaredSymbol(controllerNode);
                    var controllerName = controllerSymbol.Name.TrimEnd("Controller");

                    var genControllerClass = _controllerGenerator.GeneratePartialController(controllerSymbol, areaName, controllerName);
                    var r4ControllerClass = _controllerGenerator.GenerateR4Controller(controllerSymbol);

                    namespaceNode = namespaceNode
                        .AddMembers(genControllerClass, r4ControllerClass);
                    if (_settings.SplitIntoMutipleFiles)
                    {
                        var controllerFile = NewCompilationUnit()
                            .AddMembers(namespaceNode);
                        WriteFile(controllerFile, controllerNode.SyntaxTree.FilePath.TrimEnd(".cs") + ".generated.cs");
                        namespaceNode = SyntaxNodeHelpers.CreateNamespace(nameGroup.Key);
                    }

                    mvcStaticClass = mvcStaticClass.AddMembers(
                        SyntaxNodeHelpers.CreateFieldWithDefaultInitializer(
                            controllerName,
                            $"{nameGroup.Key}.{genControllerClass.Identifier}",
                            $"{nameGroup.Key}.{r4ControllerClass.Identifier}",
                            SyntaxKind.PublicKeyword,
                            SyntaxKind.StaticKeyword));
                }

                if (!_settings.SplitIntoMutipleFiles)
                    generatedControllers.Add(namespaceNode);
            }

            var staticFileNode = _staticFileGenerator.GenerateStaticFiles(_settings);

            // add the dummy class using in the derived controller partial class
            var r4Namespace = SyntaxNodeHelpers.CreateNamespace(_settings.R4MvcNamespace).WithDummyClass();

            var actionResultClass =
                SyntaxNodeHelpers.CreateClass(Constants.ActionResultClass, null, SyntaxKind.InternalKeyword, SyntaxKind.PartialKeyword)
                    .WithBaseTypes("ActionResult", "IR4MvcActionResult")
                    .WithMethods(ConstructorDeclaration(Constants.ActionResultClass)
                        .WithModifiers(SyntaxKind.PublicKeyword)
                        .AddParameterListParameters(
                            Parameter(Identifier("area")).WithType(SyntaxNodeHelpers.PredefinedStringType()),
                            Parameter(Identifier("controller")).WithType(SyntaxNodeHelpers.PredefinedStringType()),
                            Parameter(Identifier("action")).WithType(SyntaxNodeHelpers.PredefinedStringType()),
                            Parameter(Identifier("protocol")).WithType(SyntaxNodeHelpers.PredefinedStringType())
                                .WithDefault(EqualsValueClause(LiteralExpression(SyntaxKind.NullLiteralExpression))))
                        .WithBody(
                            Block(
                                ExpressionStatement(
                                    InvocationExpression(
                                        SyntaxNodeHelpers.MemberAccess("this", "InitMVCT4Result"))
                                        .WithArgumentList(
                                            IdentifierName("area"),
                                            IdentifierName("controller"),
                                            IdentifierName("action"),
                                            IdentifierName("protocol"))))))
                    .WithAutoStringProperty("Controller", SyntaxKind.PublicKeyword)
                    .WithAutoStringProperty("Action", SyntaxKind.PublicKeyword)
                    .WithAutoStringProperty("Protocol", SyntaxKind.PublicKeyword)
                    .WithAutoProperty("RouteValueDictionary", IdentifierName("RouteValueDictionary"), SyntaxKind.PublicKeyword);

            var jsonResultClass =
                SyntaxNodeHelpers.CreateClass(Constants.JsonResultClass, null, SyntaxKind.InternalKeyword, SyntaxKind.PartialKeyword)
                    .WithBaseTypes("JsonResult", "IR4MvcActionResult")
                    .WithMethods(ConstructorDeclaration(Constants.JsonResultClass)
                        .WithModifiers(SyntaxKind.PublicKeyword)
                        .AddParameterListParameters(
                            Parameter(Identifier("area")).WithType(SyntaxNodeHelpers.PredefinedStringType()),
                            Parameter(Identifier("controller")).WithType(SyntaxNodeHelpers.PredefinedStringType()),
                            Parameter(Identifier("action")).WithType(SyntaxNodeHelpers.PredefinedStringType()),
                            Parameter(Identifier("protocol")).WithType(SyntaxNodeHelpers.PredefinedStringType())
                                .WithDefault(EqualsValueClause(LiteralExpression(SyntaxKind.NullLiteralExpression))))
                        .WithInitializer(ConstructorInitializer(SyntaxKind.BaseConstructorInitializer, ArgumentList(SingletonSeparatedList(Argument(LiteralExpression(SyntaxKind.NullLiteralExpression))))))
                        .WithBody(
                            Block(
                                ExpressionStatement(
                                    InvocationExpression(
                                        SyntaxNodeHelpers.MemberAccess("this", "InitMVCT4Result"))
                                        .WithArgumentList(
                                            IdentifierName("area"),
                                            IdentifierName("controller"),
                                            IdentifierName("action"),
                                            IdentifierName("protocol"))))))
                    .WithAutoStringProperty("Controller", SyntaxKind.PublicKeyword)
                    .WithAutoStringProperty("Action", SyntaxKind.PublicKeyword)
                    .WithAutoStringProperty("Protocol", SyntaxKind.PublicKeyword)
                    .WithAutoProperty("RouteValueDictionary", IdentifierName("RouteValueDictionary"), SyntaxKind.PublicKeyword);

            var r4mvcNode = NewCompilationUnit()
                    .AddMembers(generatedControllers.Cast<MemberDeclarationSyntax>().ToArray())
                    .AddMembers(staticFileNode)
                    .AddMembers(r4Namespace)
                    .AddMembers(mvcStaticClass)
                    .AddMembers(actionResultClass)
                    .AddMembers(jsonResultClass);
            WriteFile(r4mvcNode, Path.Combine(projectRoot, R4MvcGenerator.R4MvcFileName));
        }

        private CompilationUnitSyntax NewCompilationUnit()
        {
            // Create the root node and add usings, header, pragma
            return SyntaxFactory.CompilationUnit()
                    .WithUsings(
                        "System.CodeDom.Compiler",
                        "System.Diagnostics",
                        "System.Threading.Tasks",
                        "Microsoft.AspNetCore.Mvc",
                        "Microsoft.AspNetCore.Routing",
                        _settings.R4MvcNamespace)
                    .WithHeader(_headerText)
                    .WithPragmaCodes(false, pragmaCodes);
        }

        public void WriteFile(CompilationUnitSyntax contents, string filePath)
        {
            contents = contents
                .NormalizeWhitespace()
                // NOTE reenable pragma codes after normalizing whitespace or it doesn't place on newline
                .WithPragmaCodes(true, pragmaCodes);

            _filePersistService.WriteFile(contents, filePath);
        }
    }
}
