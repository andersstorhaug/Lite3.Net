using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using TronDotNet.Generators.Text;
using static TronDotNet.Generators.TronApiGenerator.AttributeProperty;

namespace TronDotNet.Generators;

[Generator]
public class TronApiGenerator : IIncrementalGenerator
{
    public static class AttributeProperty
    {
        public const string
            IsTryPattern = nameof(IsTryPattern),
            KeyDataArg = nameof(KeyDataArg),
            ReturnArg = nameof(ReturnArg);
    }

    private const string
        CoreTypeName = "Lite3",
        TronTypeName = "Tron",
        TronContextTypeName = "TronContext",
        TronContextExtensionsTypeName = $"{TronContextTypeName}Extensions",
        CoreMethodPrefix = $"{CoreTypeName}.",
        CoreNamespace = $"{nameof(TronDotNet)}",
        AttributeNamespace = $"{CoreNamespace}.{nameof(Generators)}",
        AttributeName = "TronApiAttribute",
        AttributeTypeMetadataName = $"{AttributeNamespace}.{AttributeName}",
        AttributeSource =
            $$"""
             namespace {{AttributeNamespace}};

             [System.AttributeUsage(System.AttributeTargets.Method)]
             internal sealed class {{AttributeName}} : System.Attribute
             {
                 public bool {{IsTryPattern}} { get; init; }
                 public string {{KeyDataArg}} { get; init; }
                 public string {{ReturnArg}} { get; init; }
             }
             """;
    
    private const string InlineAttribute = "[MethodImpl(MethodImplOptions.AggressiveInlining)]";
    
    public void Initialize(IncrementalGeneratorInitializationContext initialization)
    {
        initialization.RegisterPostInitializationOutput(output =>
        {
            output.AddSource($"{AttributeName}.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
        });

        var methods = initialization.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeTypeMetadataName,
            predicate: static (_, _) => true,
            transform: static (syntax, _) => (IMethodSymbol)syntax.TargetSymbol);

        initialization.RegisterSourceOutput(
            methods.Collect().Combine(initialization.CompilationProvider),
            static (context, values) =>
            {
                var (methodSymbols, compilation) = values;
                
                if (methodSymbols.Length == 0)
                    return;

                var attributeType = compilation.GetTypeByMetadataName(AttributeTypeMetadataName);

                var tronPrimarySource = new StringBuilder();
                var tronContextSource = new StringBuilder();
                
                tronPrimarySource
                    .Append("namespace ").Append(CoreNamespace).AppendLine(";")
                    .AppendLine()
                    .AppendLine("using System.Runtime.CompilerServices;")
                    .Append("using static ").Append(CoreNamespace).Append(".").Append(CoreTypeName).AppendLine(";")
                    .AppendLine()
                    .Append("public static partial class ").AppendLine(TronTypeName)
                    .AppendLine("{");
                
                tronContextSource
                    .Append("namespace ").Append(CoreNamespace).AppendLine(";")
                    .AppendLine()
                    .AppendLine("using System.Runtime.CompilerServices;")
                    .Append("using static ").Append(CoreNamespace).Append(".").Append(CoreTypeName).AppendLine(";")
                    .AppendLine()
                    .Append("public static class ").AppendLine(TronContextExtensionsTypeName)
                    .AppendLine("{");
                
                foreach (var (method, methodIndex) in methodSymbols.Select(Index))
                {
                    var attribute = method
                        .GetAttributes()
                        .First(value => SymbolEqualityComparer.Default.Equals(value.AttributeClass, attributeType));

                    var tryPattern = true;
                    string? keyDataArg = null;
                    string? returnArg = null;
                    
                    foreach (var entry in attribute.NamedArguments)
                    {
                        switch (entry.Key)
                        {
                            case nameof(IsTryPattern):
                                tryPattern = entry.Value.Value as bool? ?? true;
                                break;
                            case nameof(KeyDataArg):
                                keyDataArg = entry.Value.Value as string;
                                break;
                            case nameof(ReturnArg):
                                returnArg = entry.Value.Value as string;
                                break;
                        }
                    }
                    
                    var attrData = new AttributeData(tryPattern, keyDataArg, returnArg);
                    var xmlComment = method.GetDocumentationCommentXml();

                    var writeNewLine = methodIndex > 0;
                    // ReSharper disable RedundantAssignment
                    writeNewLine |= TryWriteMethod(tronContextSource, method, attrData, xmlComment, writeNewLine, useTryPattern: true, createKeyData: true, isContextApi: true);
                    writeNewLine |= TryWriteMethod(tronContextSource, method, attrData, xmlComment, writeNewLine, useTryPattern: true, createKeyData: false, isContextApi: true);
                    writeNewLine |= TryWriteMethod(tronContextSource, method, attrData, xmlComment, writeNewLine, useTryPattern: false, createKeyData: true, isContextApi: true);
                    writeNewLine |= TryWriteMethod(tronContextSource, method, attrData, xmlComment, writeNewLine, useTryPattern: false, createKeyData: false, isContextApi: true);
                    
                    writeNewLine = methodIndex > 0;
                    writeNewLine |= TryWriteMethod(tronPrimarySource, method, attrData, xmlComment, writeNewLine, useTryPattern: true, createKeyData: true, isContextApi: false);
                    writeNewLine |= TryWriteMethod(tronPrimarySource, method, attrData, xmlComment, writeNewLine, useTryPattern: true, createKeyData: false, isContextApi: false);
                    writeNewLine |= TryWriteMethod(tronPrimarySource, method, attrData, xmlComment, writeNewLine, useTryPattern: false, createKeyData: true, isContextApi: false);
                    writeNewLine |= TryWriteMethod(tronPrimarySource, method, attrData, xmlComment, writeNewLine, useTryPattern: false, createKeyData: false, isContextApi: false);
                    // ReSharper restore RedundantAssignment
                }
                
                tronPrimarySource.AppendLine("}");
                tronContextSource.AppendLine("}");
                
                context.AddSource($"Tron.g.cs", tronPrimarySource.ToString());
                context.AddSource($"TronContextExtensions.g.cs", tronContextSource.ToString());
            }
        );
    }
    
    // Try Pattern / Use Try Pattern
    /*
        Status status;
        status = <...>;
        return status >= 0;
    */
        
    // Try Pattern / __Not__ Use Try Pattern
    /*
        Status status;
        status = <...>;
        if (status < 0) throw status.AsException();
        return <...>;
    */

    // __Not__ Try Pattern
    /*
        <...> result;
        result = <...>;
        return <...>;
    */
    
    private static bool TryWriteMethod(StringBuilder source,
        IMethodSymbol method,
        AttributeData data,
        string? xmlComment,
        bool writeNewLine,
        bool useTryPattern,
        bool createKeyData,
        bool isContextApi)
    {
        var (tryPattern, keyDataArgName, returnArgName) = data;
        
        if (useTryPattern && !tryPattern)
            return false;

        if (createKeyData && keyDataArgName == null)
            return false;

        IParameterSymbol? returnArgParam = null;
        string? returnArgTypeName = null;
        
        if (!useTryPattern && returnArgName != null) 
            returnArgParam = method.Parameters.FirstOrDefault(param => param.Name == returnArgName);
        
        if (returnArgParam != null)
            returnArgTypeName = GetTypeName(returnArgParam.Type);

        var isSetMethod = method.Name.StartsWith("Set");
        var isInitMethod = !isSetMethod && method.Name.StartsWith("Initialize");
        var isAppendMethod = !isSetMethod && method.Name.Contains("Append");
        var isChainableMethod = isInitMethod || isSetMethod || isAppendMethod;
        
        var originalReturnTypeName = method.ReturnsVoid ? "void" : GetTypeName(method.ReturnType);

        var returnTypeName =
            useTryPattern ? "bool" :
            isContextApi && isChainableMethod ? "ref TronContext" :
            returnArgParam != null ? GetTypeName(returnArgParam.Type) :
            tryPattern ? "void" :
            originalReturnTypeName;

        if (writeNewLine)
            source.AppendLine();
        
        var indent = new Indent(level: 1);

        #region XML Comment
        if (xmlComment != null)
        {
            var paramReturnLine = ReadOnlySpan<char>.Empty;
            var paramIndex = -1;
            
            foreach (var line in xmlComment.EnumerateLines())
            {
                if (line[0] != ' ' || line.Length < 4)
                    continue;
                
                var text = line[4..];

                const string paramTagPrefix = "<param name=\"";
                const string paramTagSuffix = "\">";

                var isParam = text.StartsWith(paramTagPrefix);
                if (isParam) paramIndex++;
                
                if (!useTryPattern && returnArgName != null)
                {
                    if (isParam && text[paramTagPrefix.Length..].StartsWith(returnArgName))
                    {
                        paramReturnLine = text;
                        continue;
                    }
                }

                if (createKeyData && keyDataArgName != null)
                {
                    if (isParam && text[paramTagPrefix.Length..].StartsWith(keyDataArgName))
                        continue;
                }

                if (isContextApi)
                {
                    if (isParam && paramIndex == 0)
                        source.Append(indent).AppendLine("/// <param name=\"context\">The context.</param>");
                    
                    if (isParam && text[paramTagPrefix.Length..].StartsWith("buffer"))
                        continue;

                    if (isParam && text[paramTagPrefix.Length..].StartsWith("position"))
                        continue;
                }

                if (text.StartsWith("<returns>"))
                {   
                    if (returnTypeName == "void")
                        continue;
                    
                    if (useTryPattern)
                    {
                        source
                            .Append(indent).Append("/// ")
                            .AppendLine("<returns><c>true</c> upon success; otherwise <c>false</c>.</returns>");
                        continue;
                    }

                    if (isContextApi && isChainableMethod)
                    {
                        source
                            .Append(indent).Append("/// ")
                            .AppendLine("<returns>The context, for optional method chaining.</returns>");
                        continue;
                    }

                    if (returnArgName != null && paramReturnLine != null)
                    {
                        var start = paramTagPrefix.Length + returnArgName.Length + paramTagSuffix.Length;
                        var end = paramReturnLine[start..].LastIndexOf('<') + start;

                        if (end > 0)
                        {
                            source
                                .Append(indent).Append("/// ")
                                .Append("<returns>")
                                .Append(paramReturnLine[start..end].ToString())
                                .AppendLine("</returns>");
                            continue;
                        }
                    }
                }
            
                source.Append(indent).Append("/// ").AppendLine(text.ToString());
            }
        }
        
        #endregion

        source
            .Append(indent).AppendLine(InlineAttribute)
            .Append(indent)
            .Append("public static ")
            .Append(returnTypeName)
            .Append(" ")
            .Append(useTryPattern ? "Try" : null)
            .Append(method.Name)
            .Append("(");

        var indexOffset = 0;
        foreach (var (param, index) in method.Parameters.Select(Index))
        {
            if (isContextApi && index == 0)
            {
                source.Append("this ref TronContext context");
                indexOffset++;
            }
            
            if (
                (isContextApi && param.Name == "buffer") ||
                (isContextApi && param.Name == "position") ||
                (createKeyData && param.Name == keyDataArgName) ||
                (!useTryPattern && param.Name == returnArgName))
            {
                indexOffset--;
                continue;
            }

            WriteMethodParam(source, param, index + indexOffset);
        }

        source
            .AppendLine(")")
            .Append(indent).AppendLine("{");
        
        ++indent;

        if (createKeyData)
            source
                .Append(indent).Append("var ").Append(keyDataArgName).Append(" = ")
                .Append(CoreMethodPrefix).AppendLine("GetKeyData(key);");
        
        if (returnArgParam is not null && !(isContextApi && returnArgName == "position"))
            source.Append(indent).Append(returnArgTypeName).Append(" ").Append(returnArgName).AppendLine(";");

        if (isContextApi)
        {
            source
                .Append(indent).AppendLine("ref var buffer = ref context.Buffer;")
                .Append(indent).AppendLine("ref var position = ref context.Position;");
        }

        if (tryPattern)
        {
            source
                .Append(indent)
                .AppendLine("Status status;");
        }
        else
        {
            source.Append(indent).Append(originalReturnTypeName).AppendLine(" result;");
        }

        if (isContextApi && isSetMethod)
        {
            source
                .Append(indent).AppendLine("do")
                .Append(indent).AppendLine("{")
                .Append(++indent);
        }
        else
        {
            source.Append(indent);
        }

        source
            .Append(tryPattern ? "status = " : "result = ");

        source.Append(CoreMethodPrefix).Append(method.Name).Append("(");
        
        foreach (var (param, paramIndex) in method.Parameters.Select(Index))
            WriteCallParam(source, param, paramIndex);

        source.AppendLine(");");
        
        if (isContextApi && isSetMethod)
        {
            source
                .Append(indent).AppendLine("if (status == Status.InsufficientBuffer)")
                .Append(++indent).AppendLine("status = context.Grow();");
            --indent;
            source.Append(--indent).AppendLine("} while (status == Status.GrewBuffer);");
        }

        if (tryPattern)
        {
            if (useTryPattern)
                source.Append(indent).AppendLine("return status >= 0;");
            else
            {
                source
                    .Append(indent).AppendLine("if (status < 0)")
                    .Append(++indent).AppendLine("throw status.AsException();");
                --indent;
            }
        }

        if (!useTryPattern)
        {
            if (isContextApi && isChainableMethod)
                source.Append(indent).AppendLine("return ref context;");
            else if (returnArgParam != null)
                source.Append(indent).Append("return ").Append(returnArgParam.Name).AppendLine(";");
            else if (returnTypeName != "void")
                source.Append(indent).AppendLine("return result;");
        }

        source.Append(--indent).AppendLine("}");

        return true;
    }
    
    private record AttributeData(bool IsTryPattern, string? KeyDataArgName, string? ReturnArgName);
    
    private static (T Value, int Index) Index<T>(T value, int index) => (value, index);

    private static string GetTypeName(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    
    private static string GetModifier(RefKind refKind) => refKind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        _ => ""
    };
    
    private static void WriteCallParam(StringBuilder source, IParameterSymbol param, int index)
    {
        if (index > 0)
            source.Append(", ");
        
        source.Append(GetModifier(param.RefKind)).Append(param.Name);
    }

    private static void WriteMethodParam(StringBuilder source, IParameterSymbol param, int index)
    {
        if (index > 0)
            source.Append(", ");
        
        source
            .Append(GetModifier(param.RefKind))
            .Append(param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .Append(" ")
            .Append(param.Name);
    }
}