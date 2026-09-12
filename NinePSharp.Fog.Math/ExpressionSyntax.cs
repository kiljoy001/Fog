using System.Globalization;
using System.Numerics;

namespace NinePSharp.Fog.Symbolics;

/// <summary>Admission grammar only. All arithmetic is performed by AngouriMath.</summary>
internal sealed class ExpressionSyntax(string source)
{
    private static readonly HashSet<string> Reserved = ["e", "i", "pi", "oo", "NaN", "nan", "true", "false", "RR", "CC", "ZZ", "QQ", "NN"];
    private int position;
    private int nodes;
    internal HashSet<string> Variables { get; } = new(StringComparer.Ordinal);

    internal int Validate()
    {
        int degree = Sum(0);
        Space();
        if (position != source.Length) throw new FogException("math-syntax");
        return degree;
    }

    private int Sum(int depth)
    {
        int degree = Product(depth + 1);
        while (Take('+') || Take('-')) degree = System.Math.Max(degree, Product(depth + 1));
        return degree;
    }

    private int Product(int depth)
    {
        int degree = Unary(depth + 1);
        while (true)
        {
            if (Take('*')) degree = Bound(degree + Unary(depth + 1));
            else if (Take('/'))
            {
                if (Unary(depth + 1) != 0) throw new FogException("math-domain");
            }
            else return degree;
        }
    }

    private int Unary(int depth)
    {
        Guard(depth);
        if (Take('-')) return Unary(depth + 1);
        int degree = Atom(depth + 1);
        if (Take('^'))
        {
            string exponent = Digits();
            if (!int.TryParse(exponent, NumberStyles.None, CultureInfo.InvariantCulture, out int power) || power > 1024)
                throw new FogException("math-size");
            degree = Bound(degree * power);
        }
        return degree;
    }

    private int Atom(int depth)
    {
        Guard(depth);
        if (Take('('))
        {
            int degree = Sum(depth + 1);
            if (!Take(')')) throw new FogException("math-syntax");
            return degree;
        }
        Space();
        if (position < source.Length && char.IsAsciiLetter(source[position]))
        {
            int start = position++;
            while (position < source.Length && (char.IsAsciiLetterOrDigit(source[position]) || source[position] == '_')) position++;
            string name = source[start..position];
            if (name.Length > 64) throw new FogException("math-size");
            // Reserve engine constants and set names before the engine can interpret them.
            if (Reserved.Contains(name))
                throw new FogException("math-domain");
            Variables.Add(name);
            return 1;
        }
        string number = Digits();
        if (number.Length > 1234 || BigInteger.Parse(number, CultureInfo.InvariantCulture).GetBitLength() > 4096)
            throw new FogException("math-size");
        return 0;
    }

    private string Digits()
    {
        Space();
        int start = position;
        while (position < source.Length && char.IsAsciiDigit(source[position])) position++;
        if (position == start) throw new FogException("math-syntax");
        return source[start..position];
    }

    private bool Take(char character)
    {
        Space();
        if (position == source.Length || source[position] != character) return false;
        position++;
        return true;
    }

    private void Space()
    {
        while (position < source.Length && source[position] is ' ' or '\t' or '\r' or '\n') position++;
    }

    private void Guard(int depth)
    {
        if (depth > 256 || ++nodes > 65536) throw new FogException("math-size");
    }

    private static int Bound(int degree) => degree <= 1024 ? degree : throw new FogException("math-size");
}
