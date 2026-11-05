using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Homeostat.Tests;

/// <summary>
/// The public API of every package, compared with the approved listing in <c>api/</c>. A change to the public API
/// fails this test until the listing is updated: run the tests with <c>HOMEOSTAT_ACCEPT_API=1</c>, then review the
/// diff of <c>api/*.txt</c> in the pull request. After 1.0, removing or changing a member is a breaking change.
/// </summary>
public class PublicApiTests
{
    private static readonly string[] Packages =
    [
        "Homeostat",
        "Homeostat.Artemis",
        "Homeostat.Aspire.Hosting",
        "Homeostat.AspNetCore",
        "Homeostat.Azure",
        "Homeostat.Caching",
        "Homeostat.EntityFrameworkCore",
        "Homeostat.FeatureManagement",
        "Homeostat.Grpc",
        "Homeostat.HealthChecks",
        "Homeostat.Http",
        "Homeostat.Kafka",
        "Homeostat.Kubernetes",
        "Homeostat.MongoDB",
        "Homeostat.OpenFeature",
        "Homeostat.Polly",
        "Homeostat.RabbitMQ",
        "Homeostat.Redis",
        "Homeostat.ServiceBus",
        "Homeostat.Testing",
    ];

    [Fact]
    public void Public_api_matches_the_approved_listing()
    {
        var root = RepositoryRoot();
        var accept = Environment.GetEnvironmentVariable("HOMEOSTAT_ACCEPT_API") == "1";
        var changed = new List<string>();
        foreach (var package in Packages)
        {
            var actual = PublicApi.Describe(Assembly.Load(package));
            var path = Path.Combine(root, "api", package + ".txt");
            var approved = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
            if (approved == actual)
            {
                continue;
            }

            if (accept)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, actual);
            }
            else
            {
                changed.Add($"{package}: {FirstDifference(approved ?? "", actual)}");
            }
        }

        Assert.True(changed.Count == 0,
            "The public API changed. If it is intended, run the tests with HOMEOSTAT_ACCEPT_API=1 and review api/*.txt.\n"
            + string.Join("\n", changed));
    }

    [Fact]
    public void Every_package_has_an_approved_listing()
    {
        var listings = Directory.GetFiles(Path.Combine(RepositoryRoot(), "api"), "*.txt").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        Assert.Equal(Packages.Order(StringComparer.Ordinal), listings);
    }

    private static string FirstDifference(string approved, string actual)
    {
        var a = approved.Split('\n');
        var b = actual.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : "(end)";
            var right = i < b.Length ? b[i] : "(end)";
            if (left != right)
            {
                return $"line {i + 1}: approved '{left.Trim()}', now '{right.Trim()}'";
            }
        }

        return "line endings";
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Homeostat.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Homeostat.slnx) was not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A stable, readable listing of the public and protected surface of an assembly.</summary>
internal static class PublicApi
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static string Describe(Assembly assembly)
    {
        var text = new StringBuilder();
        foreach (var group in assembly.GetExportedTypes()
            .Where(t => !t.IsNested)
            .GroupBy(t => t.Namespace ?? "")
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.Append("namespace ").Append(group.Key).Append('\n');
            foreach (var type in group.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                DescribeType(text, type, "  ");
            }
        }

        return text.ToString();
    }

    private static void DescribeType(StringBuilder text, Type type, string indent)
    {
        text.Append(indent).Append(TypeDeclaration(type)).Append('\n');
        var inner = indent + "  ";
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(f => Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)).ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                text.Append(inner).Append(field.Name).Append(" = ").Append(Convert.ToInt64(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }

            return;
        }

        if (IsDelegate(type))
        {
            return;
        }

        var lines = new List<(int Group, string Name, string Line)>();
        foreach (var field in type.GetFields(flags).Where(f => Visible(f.IsPublic, f.IsFamily, f.IsFamilyOrAssembly)))
        {
            var modifiers = Access(field.IsPublic) + (field.IsLiteral ? "const " : (field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : ""));
            var value = field.IsLiteral ? " = " + Literal(field.GetRawConstantValue()) : "";
            lines.Add((0, field.Name, $"{modifiers}{Name(field.FieldType, Nullability.Create(field))} {field.Name}{value}"));
        }

        foreach (var constructor in type.GetConstructors(flags).Where(c => Visible(c.IsPublic, c.IsFamily, c.IsFamilyOrAssembly)))
        {
            lines.Add((1, "", $"{Access(constructor.IsPublic)}{(constructor.IsStatic ? "static " : "")}{TypeName(type)}({Parameters(constructor.GetParameters())})"));
        }

        foreach (var property in type.GetProperties(flags))
        {
            var getter = property.GetMethod is { } g && Visible(g.IsPublic, g.IsFamily, g.IsFamilyOrAssembly) ? g : null;
            var setter = property.SetMethod is { } s && Visible(s.IsPublic, s.IsFamily, s.IsFamilyOrAssembly) ? s : null;
            if (getter is null && setter is null)
            {
                continue;
            }

            var accessor = (getter ?? setter)!;
            var init = setter?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m == typeof(IsExternalInit)) == true;
            var accessors = (getter is null ? "" : (getter.IsPublic || setter is null ? "" : "protected ") + "get; ")
                + (setter is null ? "" : (setter.IsPublic || getter is null ? "" : "protected ") + (init ? "init; " : "set; "));
            var indexer = property.GetIndexParameters() is { Length: > 0 } index ? $"this[{Parameters(index)}]" : property.Name;
            lines.Add((2, property.Name, $"{Access(accessor.IsPublic || getter?.IsPublic == true || setter?.IsPublic == true)}{Modifiers(accessor, type)}{Name(property.PropertyType, Nullability.Create(property))} {indexer} {{ {accessors}}}"));
        }

        foreach (var e in type.GetEvents(flags))
        {
            if (e.AddMethod is { } add && Visible(add.IsPublic, add.IsFamily, add.IsFamilyOrAssembly))
            {
                lines.Add((3, e.Name, $"{Access(add.IsPublic)}{Modifiers(add, type)}event {Name(e.EventHandlerType!, Nullability.Create(e))} {e.Name}"));
            }
        }

        foreach (var method in type.GetMethods(flags).Where(m => (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)) && Visible(m.IsPublic, m.IsFamily, m.IsFamilyOrAssembly)))
        {
            if (method.Name == "<Clone>$" || (type.IsValueType || IsRecord(type)) && method.Name is "PrintMembers")
            {
                continue;
            }

            var generic = method.IsGenericMethodDefinition ? $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>" : "";
            var constraints = method.IsGenericMethodDefinition ? Constraints(method.GetGenericArguments()) : "";
            var extension = method.IsDefined(typeof(ExtensionAttribute), false) ? "this " : "";
            lines.Add((4, method.Name, $"{Access(method.IsPublic)}{Modifiers(method, type)}{Name(method.ReturnType, Nullability.Create(method.ReturnParameter))} {method.Name}{generic}({extension}{Parameters(method.GetParameters())}){constraints}"));
        }

        // Fields, constructors, properties, events, methods; by name, then by signature.
        foreach (var line in lines.OrderBy(l => l.Group).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Line, StringComparer.Ordinal))
        {
            text.Append(inner).Append(line.Line).Append('\n');
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamORAssem)
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            DescribeType(text, nested, inner);
        }
    }

    private static bool Visible(bool isPublic, bool isFamily, bool isFamilyOrAssembly) => isPublic || isFamily || isFamilyOrAssembly;

    private static string Access(bool isPublic) => isPublic ? "public " : "protected ";

    private static bool IsDelegate(Type type) => typeof(MulticastDelegate).IsAssignableFrom(type.BaseType) && type != typeof(MulticastDelegate);

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;

    private static string Modifiers(MethodInfo method, Type declaring)
    {
        if (method.IsStatic)
        {
            return method.IsAbstract ? "static abstract " : "static ";
        }

        if (declaring.IsInterface)
        {
            return method.IsAbstract ? "" : "default ";
        }

        if (method.IsAbstract)
        {
            return "abstract ";
        }

        if (method.IsVirtual && !method.IsFinal)
        {
            return method.GetBaseDefinition() != method ? "override " : "virtual ";
        }

        return method.IsVirtual && method.GetBaseDefinition() != method ? "sealed override " : "";
    }

    private static string TypeDeclaration(Type type)
    {
        var access = type.IsPublic || type.IsNestedPublic ? "public " : "protected ";
        string kind;
        if (type.IsEnum)
        {
            kind = "enum";
        }
        else if (type.IsInterface)
        {
            kind = "interface";
        }
        else if (IsDelegate(type))
        {
            var invoke = type.GetMethod("Invoke")!;
            return $"{access}delegate {Name(invoke.ReturnType, Nullability.Create(invoke.ReturnParameter))} {TypeName(type)}({Parameters(invoke.GetParameters())})";
        }
        else if (type.IsValueType)
        {
            kind = (type.IsDefined(typeof(IsReadOnlyAttribute), false) ? "readonly " : "") + (IsRecord(type) ? "record struct" : "struct");
        }
        else
        {
            kind = (type.IsAbstract && type.IsSealed ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : "") + (IsRecord(type) ? "record" : "class");
        }

        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
        {
            bases.Add(Name(baseType, null));
        }

        var inherited = type.BaseType?.GetInterfaces() ?? [];
        bases.AddRange(type.GetInterfaces()
            .Where(i => !inherited.Contains(i) && (i.IsPublic || i.IsNestedPublic) && !(IsRecord(type) && i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEquatable<>)))
            .Select(i => Name(i, null))
            .Order(StringComparer.Ordinal));
        if (type.IsEnum)
        {
            bases = [Name(Enum.GetUnderlyingType(type), null)];
        }

        var constraints = type.IsGenericTypeDefinition ? Constraints(type.GetGenericArguments().Where(a => a.DeclaringType == type).ToArray()) : "";
        return $"{access}{kind} {TypeName(type)}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}{constraints}";
    }

    private static string TypeName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        var own = type.IsGenericTypeDefinition ? type.GetGenericArguments().Where(a => a.DeclaringType == type).ToArray() : [];
        return own.Length == 0 ? name : $"{name}<{string.Join(", ", own.Select(a => a.Name))}>";
    }

    private static string Constraints(Type[] arguments)
    {
        var text = new StringBuilder();
        foreach (var argument in arguments)
        {
            var parts = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                parts.Add("class");
            }

            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("struct");
            }

            parts.AddRange(argument.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(c => Name(c, null)).Order(StringComparer.Ordinal));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("new()");
            }

            if (parts.Count > 0)
            {
                text.Append(" where ").Append(argument.Name).Append(" : ").Append(string.Join(", ", parts));
            }
        }

        return text.ToString();
    }

    private static string Parameters(ParameterInfo[] parameters) => string.Join(", ", parameters.Select(p =>
    {
        var type = p.ParameterType;
        var prefix = "";
        if (type.IsByRef)
        {
            prefix = p.IsOut ? "out " : p.IsIn ? "in " : "ref ";
            type = type.GetElementType()!;
        }

        if (p.IsDefined(typeof(ParamArrayAttribute), false))
        {
            prefix = "params ";
        }

        var text = $"{prefix}{Name(type, Nullability.Create(p), p.ParameterType.IsByRef)} {p.Name}";
        if (!p.HasDefaultValue)
        {
            return text;
        }

        // default(SomeStruct) is stored as null.
        var value = p.RawDefaultValue is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null ? "default" : Literal(p.RawDefaultValue);
        return $"{text} = {value}";
    }));

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };

    private static string Name(Type type, NullabilityInfo? nullability, bool byRef = false)
    {
        if (byRef && nullability?.ElementType is { } element)
        {
            nullability = element;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying, nullability?.GenericTypeArguments.FirstOrDefault()) + "?";
        }

        var nullable = !type.IsValueType && nullability?.ReadState == NullabilityState.Nullable ? "?" : "";
        if (type.IsArray)
        {
            return Name(type.GetElementType()!, nullability?.ElementType) + "[" + new string(',', type.GetArrayRank() - 1) + "]" + nullable;
        }

        if (type.IsGenericParameter)
        {
            // NullabilityInfoContext misreads unconstrained type parameters used as type arguments: leave them bare.
            return type.Name;
        }

        var keyword = Keyword(type);
        if (keyword is not null)
        {
            return keyword + nullable;
        }

        var name = type.IsNested ? Name(type.DeclaringType!, null) + "." + type.Name : type.Namespace + "." + type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            var argumentNullability = nullability?.GenericTypeArguments;
            name += "<" + string.Join(", ", arguments.Select((a, i) => Name(a, argumentNullability is { Length: > 0 } n && i < n.Length ? n[i] : null))) + ">";
        }

        return name + nullable;
    }

    private static string? Keyword(Type type) => type.IsEnum ? null : Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => "bool",
        TypeCode.Byte => "byte",
        TypeCode.Char => "char",
        TypeCode.Decimal => "decimal",
        TypeCode.Double => "double",
        TypeCode.Int16 => "short",
        TypeCode.Int32 => "int",
        TypeCode.Int64 => "long",
        TypeCode.SByte => "sbyte",
        TypeCode.Single => "float",
        TypeCode.String => "string",
        TypeCode.UInt16 => "ushort",
        TypeCode.UInt32 => "uint",
        TypeCode.UInt64 => "ulong",
        _ when type == typeof(object) => "object",
        _ when type == typeof(void) => "void",
        _ => null,
    };
}
