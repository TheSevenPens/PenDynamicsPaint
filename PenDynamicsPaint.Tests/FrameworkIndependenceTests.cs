using System.Reflection;
using PenDynamicsPaint.Drawing;
using Xunit;

namespace PenDynamicsPaint.Tests;

/// <summary>
/// Pins that the drawing and document layers owe nothing to a UI framework.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about placing ink needs a windowing toolkit. Until this was done, the stroke model, the
/// smoothing filter, the curve fitter and every brush engine depended on one to say where a mark
/// goes, because <c>StrokeSample.Position</c> was an <c>Avalonia.Point</c>.
/// </para>
/// <para>
/// <b>A convention that nothing checks is a convention that lasts until the next hurry.</b> One
/// <c>using Avalonia;</c> is all it takes to put it back, and nothing would fail. So the rule is
/// checked rather than written down: no type in these namespaces may mention a framework type
/// anywhere in its shape -- base class, interface, field, property, parameter, return, or generic
/// argument.
/// </para>
/// <para>
/// Skia is not excluded, and the difference is worth stating. It is the raster backend, the thing
/// that actually turns geometry into pixels, and a brush engine that could not name a canvas would
/// have nothing to draw on. A UI framework is a way of getting a window, which none of this needs.
/// </para>
/// <para>
/// The stronger version of this test is a project boundary: move these namespaces into an assembly
/// that does not reference Avalonia and let the compiler refuse. That is worth doing and is a
/// restructure rather than a test; this holds the line until then, and would still be worth keeping
/// afterwards for anything the reference graph cannot see.
/// </para>
/// </remarks>
public class FrameworkIndependenceTests
{
    /// <summary>The namespaces that have to stay portable.</summary>
    private static readonly string[] Portable =
    [
        "PenDynamicsPaint.Drawing",
        "PenDynamicsPaint.Paint",
    ];

    /// <summary>What they are not allowed to mention.</summary>
    private const string Framework = "Avalonia";

    [Fact]
    public void The_drawing_and_document_layers_name_no_framework_type()
    {
        var offences = new List<string>();

        foreach (var type in PortableTypes())
        {
            Check(type, type.BaseType, "base type", offences);

            foreach (var contract in type.GetInterfaces())
                Check(type, contract, "interface", offences);

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(all))
                Check(type, field.FieldType, $"field {field.Name}", offences);

            foreach (var property in type.GetProperties(all))
                Check(type, property.PropertyType, $"property {property.Name}", offences);

            foreach (var method in type.GetMethods(all))
            {
                Check(type, method.ReturnType, $"return of {method.Name}", offences);
                foreach (var parameter in method.GetParameters())
                    Check(type, parameter.ParameterType, $"{method.Name}({parameter.Name})", offences);
            }

            foreach (var constructor in type.GetConstructors(all))
                foreach (var parameter in constructor.GetParameters())
                    Check(type, parameter.ParameterType, $"constructor({parameter.Name})", offences);
        }

        Assert.True(offences.Count == 0,
            $"{Framework} has got back into the portable layers:{Environment.NewLine}" +
            string.Join(Environment.NewLine, offences));
    }

    [Fact]
    public void And_the_check_would_notice()
    {
        // The control. A test that walks a type graph and finds nothing is indistinguishable from
        // one that walks nothing at all, so this hands it a type that does mention the framework
        // and requires it to say so.
        var offences = new List<string>();
        Check(typeof(FrameworkIndependenceTests), typeof(global::Avalonia.Point), "a planted field",
              offences);

        Assert.Single(offences);
    }

    [Fact]
    public void And_it_is_looking_at_the_types_that_matter()
    {
        // The other half of the control: that the namespaces named above still contain the things
        // this is meant to protect. Renaming a namespace would otherwise leave the test passing
        // over an empty set.
        var types = PortableTypes().ToList();

        Assert.Contains(types, t => t.Name == nameof(StrokeSample));
        Assert.Contains(types, t => t.Name == nameof(PathSmoother));
        Assert.Contains(types, t => t.Name == nameof(CurveFitter));
        Assert.Contains(types, t => t.Name == "PaintSession");
        Assert.True(types.Count > 10, $"only {types.Count} types found, which cannot be right");
    }

    private static IEnumerable<Type> PortableTypes() =>
        typeof(StrokeSample).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && Portable.Contains(ns));

    /// <summary>Record a use of a framework type, following generic arguments and arrays down.</summary>
    private static void Check(Type owner, Type? used, string where, List<string> offences)
    {
        if (used is null) return;

        if (used.IsArray || used.IsByRef || used.IsPointer)
        {
            Check(owner, used.GetElementType(), where, offences);
            return;
        }

        foreach (var argument in used.GetGenericArguments())
            Check(owner, argument, where, offences);

        string ns = used.Namespace ?? "";
        if (ns == Framework || ns.StartsWith(Framework + ".", StringComparison.Ordinal))
            offences.Add($"  {owner.Name}: {where} is {used.FullName}");
    }
}
