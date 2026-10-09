using Microsoft.CodeAnalysis;
using WpfXaml.Analysis.Resources;
using WpfXaml.Analysis.TypeSystem;
using WpfXaml.Language;
using WpfXaml.Language.Syntax;
using WpfXaml.Language.Text;

namespace WpfXaml.Analysis.Semantics;

internal sealed class XamlDiagnosticAnalyzer(XamlSemanticModel model)
{
    private readonly List<XamlDiagnostic> _diagnostics = [];

    public IReadOnlyList<XamlDiagnostic> Analyze(CancellationToken cancellationToken)
    {
        _diagnostics.AddRange(model.Document.Diagnostics);
        if (model.Document.Root is not { } root)
        {
            return _diagnostics;
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = model.GetElementInfo(element);
            Add(info.Error);
            if (info.Kind == ElementKind.Ignored)
            {
                continue;
            }

            foreach (var attribute in element.Attributes)
            {
                var attributeInfo = model.GetAttributeInfo(attribute);
                Add(attributeInfo.Error);
                if (attributeInfo.Kind is AttributeKind.Ignored or AttributeKind.NamespaceDeclaration or AttributeKind.XmlAttribute)
                {
                    continue;
                }

                if (model.GetMarkupExtension(attribute) is { } markup)
                {
                    AnalyzeMarkupExtension(element, markup);
                }
                else if (attributeInfo.Member is { } member && attribute.Value is { } value && attribute.ValueSpan is { } span)
                {
                    AnalyzeLiteral(attribute, member, value, span);
                }
            }
        }

        new NameScopeChecker(model, _diagnostics).Check(root);
        return _diagnostics;
    }

    private void Add(XamlDiagnostic? diagnostic)
    {
        if (diagnostic is not null)
        {
            _diagnostics.Add(diagnostic);
        }
    }

    private void AnalyzeLiteral(XamlAttribute attribute, XamlMember member, string value, TextSpan span)
    {
        if (member.IsEvent)
        {
            AnalyzeEventHandler(value, span);
            return;
        }

        if (member.IsReadOnly)
        {
            if (!member.IsAttached)
            {
                Add(XamlSemanticModel.Error(DiagnosticCodes.ReadOnlyProperty, $"The property '{member.Name}' is read-only.", attribute.Name.Span));
            }

            return;
        }

        if (member.Name == "Source" && member.Owner.Symbol.DerivesFrom(model.Types.Known.ResourceDictionary))
        {
            AnalyzeDictionarySource(value, span);
            return;
        }

        Add(ValueChecker.Check(member.ValueType, value, span));
    }

    private void AnalyzeDictionarySource(string source, TextSpan span)
    {
        if (model.ResolveResourceSource(source.Trim()) is { FilePath: { } path } && model.Resources!.Host.GetDocument(path) is null)
        {
            Add(new XamlDiagnostic(DiagnosticCodes.ResourceDictionaryNotFound,
                $"The resource dictionary '{source.Trim()}' was not found (expected at {path}).", span, XamlDiagnosticSeverity.Warning));
        }
    }

    private bool IsDeferredContent(XamlElement element) =>
        (model.Document.Root is { } root && ResourceCollector.IsDictionaryElement(root))
        || element.Ancestors().Any(a => !a.IsPropertyElement && model.GetElementInfo(a).Type?.IsTemplate == true);

    /// <summary>
    /// <c>{StaticResource Key}</c> with a key no dictionary in scope defines. Only reported when every dictionary in
    /// scope could be read; DynamicResource is never reported, since its keys are often added at run time.
    /// </summary>
    private void AnalyzeStaticResource(XamlElement scope, MarkupExtensionSyntax extension)
    {
        var argument = extension.FindArgument("ResourceKey") ?? extension.PositionalArguments.FirstOrDefault();
        if (argument is null || argument.Value.Extension is not null || argument.Value.Text.Trim() is not { Length: > 0 } key)
        {
            return;
        }

        var resources = model.GetResourceScope(scope);
        if (resources.Find(key).Count > 0 || !resources.IsComplete)
        {
            return;
        }

        // Dictionary files and template content are resolved where they end up being used: a key defined by
        // some consumer elsewhere in the solution may well be found at run time.
        if (IsDeferredContent(scope) && model.Resources?.Host.GetAllResourceKeys() is { } allKeys && allKeys.Contains(key))
        {
            return;
        }

        var suggestion = NameSuggestions.Closest(key, resources.AllDefinitions().Select(d => d.Key));
        Add(new XamlDiagnostic(DiagnosticCodes.ResourceNotFound,
            $"The resource '{key}' was not found.{NameSuggestions.DidYouMean(suggestion)}", argument.Value.Span, XamlDiagnosticSeverity.Warning));
    }

    private void AnalyzeEventHandler(string value, TextSpan span)
    {
        // Only check against code-behind we can see; without it the handler may live in generated code.
        if (model.ClassSymbol is not { } classSymbol || !classSymbol.HasSourceLocation())
        {
            return;
        }

        var handler = value.Trim();
        var exists = classSymbol.BaseTypesAndSelf().Any(t => t.GetMembers(handler).Any(m => m is IMethodSymbol));
        if (!exists)
        {
            Add(XamlSemanticModel.Error(DiagnosticCodes.EventHandlerNotFound,
                $"'{classSymbol.Name}' does not contain an event handler named '{handler}'.", span));
        }
    }

    private void AnalyzeMarkupExtension(XamlElement scope, MarkupExtensionParseResult markup)
    {
        foreach (var diagnostic in markup.Diagnostics)
        {
            _diagnostics.Add(diagnostic);
        }

        if (markup.Root is null)
        {
            return;
        }

        foreach (var extension in markup.Root.DescendantsAndSelf())
        {
            var type = model.ResolveMarkupExtensionType(scope, extension.TypeName, out var error);
            Add(error);
            if (type is null)
            {
                continue;
            }

            if (type.Symbol.DerivesFrom(model.Types.Known.StaticResourceExtension))
            {
                AnalyzeStaticResource(scope, extension);
            }

            foreach (var argument in extension.Arguments)
            {
                if (argument.IsPositional || argument.Name!.Contains('.'))
                {
                    continue;
                }

                var member = type.GetMember(argument.Name);
                if (member is null)
                {
                    var suggestion = NameSuggestions.Closest(argument.Name, type.Members.Where(m => !m.IsEvent).Select(m => m.Name));
                    Add(XamlSemanticModel.Error(DiagnosticCodes.UnknownMember,
                        $"'{argument.Name}' is not a property of '{type.XamlName}'.{NameSuggestions.DidYouMean(suggestion)}", argument.NameSpan!.Value));
                }
                else if (argument.Value.Extension is null)
                {
                    Add(ValueChecker.Check(member.ValueType, argument.Value.Text, argument.Value.Span));
                }
            }
        }
    }
}

/// <summary>Literal value checks for types with a closed set of values.</summary>
internal static class ValueChecker
{
    public static XamlDiagnostic? Check(ITypeSymbol? type, string value, TextSpan span)
    {
        if (type is null)
        {
            return null;
        }

        type = type.UnwrapNullable();
        var trimmed = value.Trim();
        if (HasCustomTypeConverter(type))
        {
            // e.g. ModifierKeysConverter accepts "Ctrl"; we can't run converters, so don't guess.
            return null;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            var names = type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsStatic && f.HasConstantValue).Select(f => f.Name).ToList();
            foreach (var part in trimmed.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0 && !long.TryParse(p, out _) && !names.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    var suggestion = NameSuggestions.Closest(p, names);
                    return XamlSemanticModel.Error(DiagnosticCodes.InvalidValue,
                        $"'{p}' is not a valid value for '{type.Name}'.{NameSuggestions.DidYouMean(suggestion)}", span);
                }
            }
        }
        else if (type.SpecialType == SpecialType.System_Boolean && !bool.TryParse(trimmed, out _))
        {
            return XamlSemanticModel.Error(DiagnosticCodes.InvalidValue, $"'{trimmed}' is not a valid Boolean (expected True or False).", span);
        }

        return null;
    }

    private static bool HasCustomTypeConverter(ITypeSymbol type) =>
        type.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "TypeConverterAttribute"
            && a.ConstructorArguments is [{ Value: var converter }]
            && (converter as ITypeSymbol)?.Name != "EnumConverter"
            && !(converter as string ?? string.Empty).StartsWith("System.ComponentModel.EnumConverter", StringComparison.Ordinal));
}

/// <summary>Reports duplicate <c>x:Name</c>/<c>Name</c> values. Templates start a new namescope.</summary>
internal sealed class NameScopeChecker(XamlSemanticModel model, List<XamlDiagnostic> diagnostics)
{
    public void Check(XamlElement root) => Visit(root, new Dictionary<string, XamlAttribute>(StringComparer.Ordinal));

    private void Visit(XamlElement element, Dictionary<string, XamlAttribute> scope)
    {
        var info = model.GetElementInfo(element);
        if (info.Kind == ElementKind.Ignored)
        {
            return;
        }

        if (GetNameAttribute(element, info) is { Value: { Length: > 0 } name } attribute)
        {
            if (scope.ContainsKey(name))
            {
                diagnostics.Add(XamlSemanticModel.Error(DiagnosticCodes.DuplicateName,
                    $"The name '{name}' is already used in this namescope.", attribute.ValueSpan ?? attribute.Name.Span));
            }
            else
            {
                scope[name] = attribute;
            }
        }

        var childScope = info.Type?.IsTemplate == true ? new Dictionary<string, XamlAttribute>(StringComparer.Ordinal) : scope;
        foreach (var child in element.ChildElements)
        {
            Visit(child, childScope);
        }
    }

    private XamlAttribute? GetNameAttribute(XamlElement element, ElementInfo info)
    {
        foreach (var attribute in element.Attributes)
        {
            if (attribute.Name.LocalName != "Name")
            {
                continue;
            }

            if (attribute.Name.Prefix is not null && model.IsXamlLanguageNamespace(element, attribute.Name.Prefix))
            {
                return attribute;
            }

            if (attribute.Name.Prefix is null && info.Type?.RuntimeNamePropertyName == "Name")
            {
                return attribute;
            }
        }

        return null;
    }
}
