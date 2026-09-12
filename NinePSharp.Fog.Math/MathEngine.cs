using System.Globalization;
using System.Text;
using AngouriMath;

namespace NinePSharp.Fog.Symbolics;

/// <summary>Trusted engine entry point. Untrusted source must reach this only inside the job sandbox.</summary>
public static class MathEngine
{
    public const string ProviderVersion = "angouri-2.4.0-demo1";
    public static readonly FogRecordSchema ResultSchema = new("fogmath-result-v1", ["kind", "value"], ["kind", "value"], ["kind", "value"]);

    public static byte[] Execute(byte[] source, string operation, string? variable, int outputBytes, CancellationToken cancellation = default)
    {
        (string text, ExpressionSyntax syntax) = ValidateSource(source, operation, variable);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        MathS.Multithreading.SetLocalCancellationToken(cancellation);
        using var noNewton = MathS.Settings.AllowNewton.Set(false);
        try
        {
            Entity expression = MathS.FromString(text, useCache: false);
            ValidateTree(expression, syntax.Variables, false);
            IEnumerable<string> values = Calculate(expression, operation, variable, syntax.Variables);
            return ResultSchema.Serialize(values.Select(value => new Dictionary<string, string?>
            {
                ["kind"] = operation == "solve" ? "root" : "expression",
                ["value"] = value,
            }), outputBytes, 65536);
        }
        catch (FogException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new FogException("math-result"); }
        finally
        {
            MathS.Multithreading.SetLocalCancellationToken(CancellationToken.None);
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static (string, ExpressionSyntax) ValidateSource(byte[] source, string operation, string? variable)
    {
        if (source.Length > 1048576) throw new FogException("math-size");
        ValidateOperation(operation, variable);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(source); }
        catch (DecoderFallbackException) { throw new FogException("math-syntax"); }
        var syntax = new ExpressionSyntax(text);
        int degree = syntax.Validate();
        if (operation == "evaluate" && syntax.Variables.Count != 0 ||
            operation == "solve" && (degree > 4 || syntax.Variables.Any(name => name != variable))) throw new FogException("math-domain");
        if (variable is not null)
        {
            var variableSyntax = new ExpressionSyntax(variable);
            if (variableSyntax.Validate() != 1 || !variableSyntax.Variables.SetEquals([variable])) throw new FogException("math-domain");
        }

        return (text, syntax);
    }

    private static void ValidateOperation(string operation, string? variable)
    {
        if (operation is not ("evaluate" or "simplify" or "differentiate" or "solve") ||
            ((operation is "differentiate" or "solve") != (variable is not null))) throw new FogException("math-domain");
    }

    private static IEnumerable<string> Calculate(Entity expression, string operation, string? variable, IReadOnlySet<string> variables)
    {
        Entity answer = operation switch
        {
            "evaluate" => expression.InnerSimplified,
            "differentiate" => expression.Differentiate(variable!).Simplify(),
            _ => expression.Simplify(),
        };
        if (operation == "solve") return Solve(answer, variable!);
        if (operation == "evaluate" && answer is not Entity.Number.Rational) throw new FogException("math-domain");
        return [Print(answer, variables, false)];
    }

    private static string[] Solve(Entity answer, string variable)
    {
        if (answer == 0) throw new FogException("math-nonfinite");
        if (answer.SolveEquation(variable) is not Entity.Set.FiniteSet roots) throw new FogException("math-unresolved");
        return roots.Select(root => root.Simplify()).Where(root => root.EvalNumerical() is Entity.Number.Real)
            .Select(root => Print(root, new HashSet<string>(), true)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Print(Entity value, IReadOnlySet<string> variables, bool roots)
    {
        ValidateTree(value, variables, roots);
        if (!value.IsFinite) throw new FogException("math-nonfinite");
        return value.ToString();
    }

    private static void ValidateTree(Entity expression, IReadOnlySet<string> variables, bool roots)
    {
        var pending = new Stack<(Entity Node, int Depth)>();
        pending.Push((expression, 0));
        int nodes = 0;
        while (pending.TryPop(out var entry))
        {
            if (++nodes > 65536 || entry.Depth > 256) throw new FogException("math-size");
            ValidateNode(entry.Node, variables, roots);
            foreach (Entity child in entry.Node.DirectChildren) pending.Push((child, entry.Depth + 1));
        }
    }
    private static void ValidateNode(Entity node, IReadOnlySet<string> variables, bool roots)
    {
        if (node is Entity.Number.Rational rational) ValidateNumber(rational);
        else if (node is Entity.Variable symbol) ValidateVariable(symbol, variables);
        else if (node is Entity.Divf division) ValidateDivision(division, roots);
        else if (node is Entity.Powf power) ValidatePower(power, roots);
        else if (node is not (Entity.Sumf or Entity.Minusf or Entity.Mulf)) throw new FogException("math-domain");
    }

    private static void ValidateNumber(Entity.Number.Rational rational)
    {
        if (rational.Numerator.EInteger.GetUnsignedBitLengthAsInt64() > 4096 || rational.Denominator.EInteger.GetUnsignedBitLengthAsInt64() > 4096)
            throw new FogException("math-size");
    }

    private static void ValidateVariable(Entity.Variable symbol, IReadOnlySet<string> variables)
    {
        if (!variables.Contains(symbol.Name)) throw new FogException("math-domain");
    }

    private static void ValidateDivision(Entity.Divf division, bool roots)
    {
        if (!roots && (division.DirectChildren.Last().InnerSimplified is not Entity.Number.Rational denominator || denominator == 0))
            throw new FogException("math-domain");
    }

    private static void ValidatePower(Entity.Powf power, bool roots)
    {
        if (!roots && power.Exponent is not Entity.Number.Integer) throw new FogException("math-domain");
    }

}
