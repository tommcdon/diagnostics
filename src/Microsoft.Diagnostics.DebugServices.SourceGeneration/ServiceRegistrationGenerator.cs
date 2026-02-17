// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.Diagnostics.DebugServices.SourceGeneration
{
    /// <summary>
    /// Roslyn incremental source generator that scans for [ServiceExport], [ProviderExport],
    /// [ServiceImport], and [Command] attributes and emits strongly-typed registration code
    /// that replaces runtime reflection, making the code trim/Native AOT friendly.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ServiceRegistrationGenerator : IIncrementalGenerator
    {
        // Fully qualified attribute names
        private const string ServiceExportAttributeName = "Microsoft.Diagnostics.DebugServices.ServiceExportAttribute";
        private const string ProviderExportAttributeName = "Microsoft.Diagnostics.DebugServices.ProviderExportAttribute";
        private const string ServiceImportAttributeName = "Microsoft.Diagnostics.DebugServices.ServiceImportAttribute";
        private const string CommandAttributeName = "Microsoft.Diagnostics.DebugServices.CommandAttribute";


        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Collect [ServiceExport] on classes
            IncrementalValuesProvider<ServiceExportInfo> serviceExportClasses = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    ServiceExportAttributeName,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, ct) => ExtractServiceExportFromClass(ctx, ct))
                .Where(static info => info != null);

            // Collect [ServiceExport] on static methods
            IncrementalValuesProvider<ServiceExportInfo> serviceExportMethods = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    ServiceExportAttributeName,
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (ctx, ct) => ExtractServiceExportFromMethod(ctx, ct))
                .Where(static info => info != null);

            // Collect [ProviderExport] on classes
            IncrementalValuesProvider<ProviderExportInfo> providerExportClasses = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    ProviderExportAttributeName,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, ct) => ExtractProviderExportFromClass(ctx, ct))
                .Where(static info => info != null);

            // Collect [ProviderExport] on static methods
            IncrementalValuesProvider<ProviderExportInfo> providerExportMethods = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    ProviderExportAttributeName,
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (ctx, ct) => ExtractProviderExportFromMethod(ctx, ct))
                .Where(static info => info != null);

            // Collect [Command] on classes
            IncrementalValuesProvider<CommandInfo> commands = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    CommandAttributeName,
                    predicate: static (node, _) => node is ClassDeclarationSyntax,
                    transform: static (ctx, ct) => ExtractCommand(ctx, ct))
                .Where(static info => info != null);

            // Combine all service exports
            IncrementalValueProvider<ImmutableArray<ServiceExportInfo>> allServiceExports =
                serviceExportClasses.Collect().Combine(serviceExportMethods.Collect())
                    .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

            // Combine all provider exports
            IncrementalValueProvider<ImmutableArray<ProviderExportInfo>> allProviderExports =
                providerExportClasses.Collect().Combine(providerExportMethods.Collect())
                    .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

            // Combine all commands
            IncrementalValueProvider<ImmutableArray<CommandInfo>> allCommands = commands.Collect();

            // Combine everything with assembly name and generate
            IncrementalValueProvider<string> assemblyName = context.CompilationProvider
                .Select(static (compilation, _) => compilation.AssemblyName ?? "Unknown");

            IncrementalValueProvider<(ImmutableArray<ServiceExportInfo> Services, ImmutableArray<ProviderExportInfo> Providers, ImmutableArray<CommandInfo> Commands, string AssemblyName)> combined =
                allServiceExports.Combine(allProviderExports).Combine(allCommands).Combine(assemblyName)
                    .Select(static (pair, _) => (pair.Left.Left.Left, pair.Left.Left.Right, pair.Left.Right, pair.Right));

            context.RegisterSourceOutput(combined, static (spc, data) => GenerateRegistration(spc, data.Services, data.Providers, data.Commands, data.AssemblyName));

            // Emit polyfills for trim/AOT analysis attributes needed by generated code on netstandard2.0.
            // On .NET 5+ these are provided by the BCL.
            context.RegisterPostInitializationOutput(static ctx =>
            {
                ctx.AddSource("TrimPolyfills.g.cs", @"// <auto-generated/>
#if !NET5_0_OR_GREATER
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter |
                     AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Method |
                     AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct,
                     Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
        {
            MemberTypes = memberTypes;
        }

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0003,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None
    }

    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
    internal sealed class DynamicDependencyAttribute : Attribute
    {
        public DynamicDependencyAttribute(DynamicallyAccessedMemberTypes memberTypes, Type type) { }
    }
}
#endif
");
            });
        }

        #region Extraction Methods

        private static ServiceExportInfo ExtractServiceExportFromClass(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            INamedTypeSymbol classSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
            AttributeData attr = ctx.Attributes[0];

            string scope = "Global";
            string registeredType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == "Scope" && named.Value.Value is int scopeVal)
                {
                    scope = GetScopeName(scopeVal);
                }
                else if (named.Key == "Type" && named.Value.Value is INamedTypeSymbol typeSymbol)
                {
                    registeredType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }

            // Get constructor info
            IMethodSymbol constructor = classSymbol.InstanceConstructors
                .FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared);

            // If no explicit public constructor, check for the default
            constructor ??= classSymbol.InstanceConstructors
                .FirstOrDefault(c => c.Parameters.Length == 0);

            if (constructor == null)
            {
                return null;
            }

            List<ParameterInfo> ctorParams = ExtractParameters(constructor);
            List<ImportMemberInfo> importMembers = ExtractImportMembers(classSymbol);

            return new ServiceExportInfo
            {
                Scope = scope,
                RegisteredType = registeredType,
                ImplementationType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsFactoryMethod = false,
                ConstructorParameters = ctorParams,
                ImportMembers = importMembers
            };
        }

        private static ServiceExportInfo ExtractServiceExportFromMethod(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            IMethodSymbol methodSymbol = (IMethodSymbol)ctx.TargetSymbol;
            if (!methodSymbol.IsStatic || methodSymbol.DeclaredAccessibility != Accessibility.Public)
            {
                return null;
            }

            AttributeData attr = ctx.Attributes[0];

            string scope = "Global";
            string registeredType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == "Scope" && named.Value.Value is int scopeVal)
                {
                    scope = GetScopeName(scopeVal);
                }
                else if (named.Key == "Type" && named.Value.Value is INamedTypeSymbol typeSymbol)
                {
                    registeredType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }

            List<ParameterInfo> methodParams = ExtractParameters(methodSymbol);

            return new ServiceExportInfo
            {
                Scope = scope,
                RegisteredType = registeredType,
                ImplementationType = methodSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsFactoryMethod = true,
                FactoryMethodName = methodSymbol.Name,
                ConstructorParameters = methodParams,
                ImportMembers = new()
            };
        }

        private static ProviderExportInfo ExtractProviderExportFromClass(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            INamedTypeSymbol classSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
            AttributeData attr = ctx.Attributes[0];

            string registeredType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == "Type" && named.Value.Value is INamedTypeSymbol typeSymbol)
                {
                    registeredType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }

            IMethodSymbol constructor = classSymbol.InstanceConstructors
                .FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared);
            constructor ??= classSymbol.InstanceConstructors
                .FirstOrDefault(c => c.Parameters.Length == 0);

            if (constructor == null)
            {
                return null;
            }

            List<ParameterInfo> ctorParams = ExtractParameters(constructor);
            List<ImportMemberInfo> importMembers = ExtractImportMembers(classSymbol);

            return new ProviderExportInfo
            {
                RegisteredType = registeredType,
                ImplementationType = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsFactoryMethod = false,
                ConstructorParameters = ctorParams,
                ImportMembers = importMembers
            };
        }

        private static ProviderExportInfo ExtractProviderExportFromMethod(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            IMethodSymbol methodSymbol = (IMethodSymbol)ctx.TargetSymbol;
            if (!methodSymbol.IsStatic || methodSymbol.DeclaredAccessibility != Accessibility.Public)
            {
                return null;
            }

            AttributeData attr = ctx.Attributes[0];
            string registeredType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
            {
                if (named.Key == "Type" && named.Value.Value is INamedTypeSymbol typeSymbol)
                {
                    registeredType = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
            }

            List<ParameterInfo> methodParams = ExtractParameters(methodSymbol);

            return new ProviderExportInfo
            {
                RegisteredType = registeredType,
                ImplementationType = methodSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsFactoryMethod = true,
                FactoryMethodName = methodSymbol.Name,
                ConstructorParameters = methodParams,
                ImportMembers = new()
            };
        }

        private static CommandInfo ExtractCommand(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            INamedTypeSymbol classSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
            if (classSymbol.IsAbstract)
            {
                return null;
            }

            string fullTypeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            // Collect constructor params
            IMethodSymbol constructor = classSymbol.InstanceConstructors
                .FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared);
            constructor ??= classSymbol.InstanceConstructors
                .FirstOrDefault(c => c.Parameters.Length == 0);

            List<ParameterInfo> ctorParams = constructor != null ? ExtractParameters(constructor) : new();
            List<ImportMemberInfo> importMembers = ExtractImportMembers(classSymbol);

            return new CommandInfo
            {
                FullTypeName = fullTypeName,
                ConstructorParameters = ctorParams,
                ImportMembers = importMembers
            };
        }

        #endregion

        #region Helper Methods

        private static List<ParameterInfo> ExtractParameters(IMethodSymbol method)
        {
            List<ParameterInfo> result = new();
            foreach (IParameterSymbol param in method.Parameters)
            {
                bool optional = false;
                foreach (AttributeData attr in param.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() == ServiceImportAttributeName)
                    {
                        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
                        {
                            if (named.Key == "Optional" && named.Value.Value is bool opt)
                            {
                                optional = opt;
                            }
                        }
                    }
                }
                result.Add(new ParameterInfo
                {
                    TypeName = param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Optional = optional
                });
            }
            return result;
        }

        private static List<ImportMemberInfo> ExtractImportMembers(INamedTypeSymbol classSymbol)
        {
            List<ImportMemberInfo> result = new();

            // Walk the type hierarchy
            for (INamedTypeSymbol current = classSymbol; current != null; current = current.BaseType)
            {
                if (current.SpecialType == SpecialType.System_Object || current.SpecialType == SpecialType.System_ValueType)
                {
                    break;
                }

                foreach (ISymbol member in current.GetMembers())
                {
                    if (member is IFieldSymbol field)
                    {
                        foreach (AttributeData attr in field.GetAttributes())
                        {
                            if (attr.AttributeClass?.ToDisplayString() == ServiceImportAttributeName)
                            {
                                bool optional = false;
                                foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
                                {
                                    if (named.Key == "Optional" && named.Value.Value is bool opt)
                                    {
                                        optional = opt;
                                    }
                                }

                                // Include all fields regardless of accessibility
                                // Public/internal non-readonly fields can be set directly; others need reflection
                                bool canSetDirectly = (field.DeclaredAccessibility == Accessibility.Public ||
                                                       field.DeclaredAccessibility == Accessibility.Internal) &&
                                                      !field.IsReadOnly;
                                result.Add(new ImportMemberInfo
                                {
                                    MemberName = field.Name,
                                    TypeName = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                    Optional = optional,
                                    IsReadOnly = field.IsReadOnly,
                                    NeedsReflection = !canSetDirectly,
                                    DeclaringTypeName = field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                    Kind = ImportMemberKind.Field
                                });
                            }
                        }
                    }
                    else if (member is IPropertySymbol prop && prop.SetMethod != null)
                    {
                        foreach (AttributeData attr in prop.GetAttributes())
                        {
                            if (attr.AttributeClass?.ToDisplayString() == ServiceImportAttributeName)
                            {
                                bool optional = false;
                                foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
                                {
                                    if (named.Key == "Optional" && named.Value.Value is bool opt)
                                    {
                                        optional = opt;
                                    }
                                }

                                // Include all properties regardless of accessibility
                                // Public/internal setters can be set directly; others need reflection
                                bool canSetDirectly = prop.SetMethod.DeclaredAccessibility == Accessibility.Public ||
                                                      prop.SetMethod.DeclaredAccessibility == Accessibility.Internal;
                                result.Add(new ImportMemberInfo
                                {
                                    MemberName = prop.Name,
                                    TypeName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                    Optional = optional,
                                    NeedsReflection = !canSetDirectly,
                                    DeclaringTypeName = prop.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                    Kind = ImportMemberKind.Property
                                });
                            }
                        }
                    }
                    else if (member is IMethodSymbol method && !method.IsStatic && method.MethodKind == MethodKind.Ordinary)
                    {
                        foreach (AttributeData attr in method.GetAttributes())
                        {
                            if (attr.AttributeClass?.ToDisplayString() == ServiceImportAttributeName)
                            {
                                bool canAccess = method.DeclaredAccessibility == Accessibility.Public ||
                                                 method.DeclaredAccessibility == Accessibility.Internal;
                                if (canAccess)
                                {
                                    List<ParameterInfo> methodParams = ExtractParameters(method);
                                    result.Add(new ImportMemberInfo
                                    {
                                        MemberName = method.Name,
                                        TypeName = null,
                                        Optional = false,
                                        Kind = ImportMemberKind.Method,
                                        MethodParameters = methodParams
                                    });
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static string GetScopeName(int scopeVal)
        {
            switch (scopeVal)
            {
                case 0: return "Global";
                case 1: return "Context";
                case 2: return "Target";
                case 3: return "Module";
                case 4: return "Thread";
                case 5: return "Runtime";
                default: return "Global";
            }
        }

        #endregion

        #region Code Generation

        private static void GenerateRegistration(
            SourceProductionContext spc,
            ImmutableArray<ServiceExportInfo> services,
            ImmutableArray<ProviderExportInfo> providers,
            ImmutableArray<CommandInfo> commands,
            string assemblyName)
        {
            if (services.IsEmpty && providers.IsEmpty && commands.IsEmpty)
            {
                return;
            }

            // Use a sanitized assembly name for the namespace to ensure uniqueness across assemblies
            string sanitizedName = assemblyName.Replace(".", "").Replace("-", "").Replace(" ", "");

            StringBuilder sb = new();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Licensed to the .NET Foundation under one or more agreements.");
            sb.AppendLine("// The .NET Foundation licenses this file to you under the MIT license.");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using Microsoft.Diagnostics.DebugServices;");
            sb.AppendLine();
            sb.AppendLine($"namespace Microsoft.Diagnostics.DebugServices.Generated.{sanitizedName}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Auto-generated service and command registration that replaces reflection-based discovery.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class ServiceRegistration");
            sb.AppendLine("    {");

            // Generate RegisterServices method
            if (!services.IsEmpty || !providers.IsEmpty)
            {
                GenerateRegisterServicesMethod(sb, services, providers);
            }

            // Generate RegisterCommands method
            if (!commands.IsEmpty)
            {
                GenerateRegisterCommandsMethod(sb, commands);
            }

            // Generate factory methods for services
            int factoryIndex = 0;
            foreach (ServiceExportInfo service in services)
            {
                GenerateFactoryMethod(sb, service, $"CreateService_{factoryIndex}");
                factoryIndex++;
            }

            // Generate factory methods for providers
            int providerIndex = 0;
            foreach (ProviderExportInfo provider in providers)
            {
                GenerateProviderFactoryMethod(sb, provider, $"CreateProvider_{providerIndex}");
                providerIndex++;
            }

            // Generate factory methods for commands
            int commandIndex = 0;
            foreach (CommandInfo cmd in commands)
            {
                GenerateCommandFactoryMethod(sb, cmd, $"CreateCommand_{commandIndex}");
                commandIndex++;
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource("ServiceRegistration.g.cs", sb.ToString());
        }

        private static void GenerateRegisterServicesMethod(
            StringBuilder sb,
            ImmutableArray<ServiceExportInfo> services,
            ImmutableArray<ProviderExportInfo> providers)
        {
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Registers all [ServiceExport] and [ProviderExport] services found in this assembly.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <param name=\"addServiceFactory\">Callback to register a service factory (scope, type, factory).</param>");
            sb.AppendLine("        /// <param name=\"addProviderFactory\">Callback to register a provider factory (type, factory).</param>");
            sb.AppendLine("        public static void RegisterServices(Action<ServiceScope, Type, ServiceFactory> addServiceFactory, Action<Type, ServiceFactory> addProviderFactory)");
            sb.AppendLine("        {");

            int factoryIndex = 0;
            foreach (ServiceExportInfo service in services)
            {
                sb.AppendLine($"            addServiceFactory(ServiceScope.{service.Scope}, typeof({service.RegisteredType}), CreateService_{factoryIndex});");
                factoryIndex++;
            }

            int providerIndex = 0;
            foreach (ProviderExportInfo provider in providers)
            {
                sb.AppendLine($"            addProviderFactory(typeof({provider.RegisteredType}), CreateProvider_{providerIndex});");
                providerIndex++;
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateRegisterCommandsMethod(StringBuilder sb, ImmutableArray<CommandInfo> commands)
        {
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Registers all [Command]-attributed types found in this assembly.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <param name=\"addCommands\">Callback to register a command type with its factory (type, factory).</param>");

            // Add [DynamicDependency] for each command type so the AOT linker preserves
            // public properties (with [Argument]/[Option] attributes) and public methods (with [FilterInvoke]/[HelpInvoke]).
            foreach (CommandInfo cmd in commands)
            {
                sb.AppendLine($"        [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods, typeof({cmd.FullTypeName}))]");
            }

            sb.AppendLine("        public static void RegisterCommands(Action<Type, Func<IServiceProvider, object>> addCommands)");
            sb.AppendLine("        {");

            int commandIndex = 0;
            foreach (CommandInfo cmd in commands)
            {
                sb.AppendLine($"            addCommands(typeof({cmd.FullTypeName}), CreateCommand_{commandIndex});");
                commandIndex++;
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateFactoryMethod(StringBuilder sb, ServiceExportInfo service, string methodName)
        {
            sb.AppendLine($"        private static object {methodName}(IServiceProvider services)");
            sb.AppendLine("        {");

            if (service.IsFactoryMethod)
            {
                // Call the static factory method
                GenerateStaticMethodCall(sb, service.ImplementationType, service.FactoryMethodName, service.ConstructorParameters, "instance");
                // Factory methods may return instances that also need import injection
                sb.AppendLine("            return instance;");
            }
            else
            {
                // Call the constructor
                GenerateConstructorCall(sb, service.ImplementationType, service.ConstructorParameters, "instance");
                // Set ServiceImport members
                GenerateImportMemberAssignments(sb, "instance", service.ImportMembers);
                sb.AppendLine("            return instance;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateProviderFactoryMethod(StringBuilder sb, ProviderExportInfo provider, string methodName)
        {
            sb.AppendLine($"        private static object {methodName}(IServiceProvider services)");
            sb.AppendLine("        {");

            if (provider.IsFactoryMethod)
            {
                GenerateStaticMethodCall(sb, provider.ImplementationType, provider.FactoryMethodName, provider.ConstructorParameters, "instance");
                sb.AppendLine("            return instance;");
            }
            else
            {
                GenerateConstructorCall(sb, provider.ImplementationType, provider.ConstructorParameters, "instance");
                GenerateImportMemberAssignments(sb, "instance", provider.ImportMembers);
                sb.AppendLine("            return instance;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateCommandFactoryMethod(StringBuilder sb, CommandInfo cmd, string methodName)
        {
            sb.AppendLine($"        private static object {methodName}(IServiceProvider services)");
            sb.AppendLine("        {");

            if (cmd.ConstructorParameters.Count == 0)
            {
                sb.AppendLine($"            {cmd.FullTypeName} instance = new {cmd.FullTypeName}();");
            }
            else
            {
                GenerateConstructorCall(sb, cmd.FullTypeName, cmd.ConstructorParameters, "instance");
            }

            GenerateImportMemberAssignments(sb, "instance", cmd.ImportMembers);
            sb.AppendLine("            return instance;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateConstructorCall(StringBuilder sb, string typeName, List<ParameterInfo> parameters, string varName)
        {
            if (parameters.Count == 0)
            {
                sb.AppendLine($"            {typeName} {varName} = new {typeName}();");
            }
            else
            {
                sb.Append($"            {typeName} {varName} = new {typeName}(");
                for (int i = 0; i < parameters.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append($"({parameters[i].TypeName})services.GetService(typeof({parameters[i].TypeName}))");
                }
                sb.AppendLine(");");
            }
        }

        private static void GenerateStaticMethodCall(StringBuilder sb, string containingType, string methodName, List<ParameterInfo> parameters, string varName)
        {
            sb.Append($"            object {varName} = {containingType}.{methodName}(");
            for (int i = 0; i < parameters.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append($"({parameters[i].TypeName})services.GetService(typeof({parameters[i].TypeName}))");
            }
            sb.AppendLine(");");
        }

        private static void GenerateImportMemberAssignments(StringBuilder sb, string varName, List<ImportMemberInfo> members)
        {
            bool needsReflection = members.Any(m => m.NeedsReflection);
            if (needsReflection)
            {
                sb.AppendLine("            System.Reflection.BindingFlags bindingFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;");
            }

            foreach (ImportMemberInfo member in members)
            {
                if (member.Kind == ImportMemberKind.Method)
                {
                    // Call the import method with resolved services
                    sb.Append($"            {varName}.{member.MemberName}(");
                    for (int i = 0; i < member.MethodParameters.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append($"({member.MethodParameters[i].TypeName})services.GetService(typeof({member.MethodParameters[i].TypeName}))");
                    }
                    sb.AppendLine(");");
                }
                else if (member.NeedsReflection)
                {
                    // Use reflection for private/protected/readonly members
                    if (member.Kind == ImportMemberKind.Field)
                    {
                        sb.AppendLine($"            typeof({member.DeclaringTypeName}).GetField(\"{member.MemberName}\", bindingFlags)?.SetValue({varName}, services.GetService(typeof({member.TypeName})));");
                    }
                    else
                    {
                        sb.AppendLine($"            typeof({member.DeclaringTypeName}).GetProperty(\"{member.MemberName}\", bindingFlags)?.SetValue({varName}, services.GetService(typeof({member.TypeName})));");
                    }
                }
                else
                {
                    // Set field or property directly
                    sb.AppendLine($"            {varName}.{member.MemberName} = ({member.TypeName})services.GetService(typeof({member.TypeName}));");
                }
            }
        }

        #endregion

        #region Data Models

        private sealed class ServiceExportInfo
        {
            public string Scope { get; set; }
            public string RegisteredType { get; set; }
            public string ImplementationType { get; set; }
            public bool IsFactoryMethod { get; set; }
            public string FactoryMethodName { get; set; }
            public List<ParameterInfo> ConstructorParameters { get; set; }
            public List<ImportMemberInfo> ImportMembers { get; set; }
        }

        private sealed class ProviderExportInfo
        {
            public string RegisteredType { get; set; }
            public string ImplementationType { get; set; }
            public bool IsFactoryMethod { get; set; }
            public string FactoryMethodName { get; set; }
            public List<ParameterInfo> ConstructorParameters { get; set; }
            public List<ImportMemberInfo> ImportMembers { get; set; }
        }

        private sealed class CommandInfo
        {
            public string FullTypeName { get; set; }
            public List<ParameterInfo> ConstructorParameters { get; set; }
            public List<ImportMemberInfo> ImportMembers { get; set; }
        }

        private sealed class ParameterInfo
        {
            public string TypeName { get; set; }
            public bool Optional { get; set; }
        }

        private sealed class ImportMemberInfo
        {
            public string MemberName { get; set; }
            public string TypeName { get; set; }
            public bool Optional { get; set; }
            public bool IsReadOnly { get; set; }
            public bool NeedsReflection { get; set; }
            public string DeclaringTypeName { get; set; }
            public ImportMemberKind Kind { get; set; }
            public List<ParameterInfo> MethodParameters { get; set; }
        }

        private enum ImportMemberKind
        {
            Field,
            Property,
            Method
        }

        #endregion
    }
}
