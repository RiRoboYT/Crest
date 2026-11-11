using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Crest.Helpers
{
    /// <summary>
    /// The quick calculator on the Tools page: plain maths ("12*7+5", "2^10", "sqrt(81)", "15% of 240")
    /// and unit conversions ("5 km in miles", "72 f to c", "2.5 gb in mb").
    /// </summary>
    public static class Calculator
    {
        /// <summary>Returns a display string like "89" or "3.11 mi", or null when the text isn't valid yet.</summary>
        public static string? Evaluate(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string text = input.Trim().ToLowerInvariant().Replace(",", "").Replace("×", "*").Replace("÷", "/");
            try
            {
                var conversion = TryConvert(text);
                if (conversion != null) return conversion;

                // "15% of 240"
                int of = text.IndexOf("% of ", StringComparison.Ordinal);
                if (of > 0)
                {
                    double pct = new Parser(text[..of]).Parse();
                    double baseValue = new Parser(text[(of + 5)..]).Parse();
                    return Format(pct / 100 * baseValue);
                }

                return Format(new Parser(text).Parse());
            }
            catch
            {
                return null;
            }
        }

        private static string Format(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "can't divide by zero";
            double abs = Math.Abs(v);
            if (abs != 0 && (abs >= 1e15 || abs < 1e-6)) return v.ToString("0.####e+0", CultureInfo.InvariantCulture);
            return Math.Round(v, 6).ToString("#,0.######", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ units

        // unit → (group, factor to the group's base unit)
        private static readonly Dictionary<string, (string Group, double Factor, string Label)> Units = new()
        {
            // length (base: metre)
            ["mm"] = ("len", 0.001, "mm"), ["cm"] = ("len", 0.01, "cm"), ["m"] = ("len", 1, "m"), ["km"] = ("len", 1000, "km"),
            ["in"] = ("len", 0.0254, "in"), ["inch"] = ("len", 0.0254, "in"), ["inches"] = ("len", 0.0254, "in"),
            ["ft"] = ("len", 0.3048, "ft"), ["foot"] = ("len", 0.3048, "ft"), ["feet"] = ("len", 0.3048, "ft"),
            ["yd"] = ("len", 0.9144, "yd"), ["yard"] = ("len", 0.9144, "yd"), ["yards"] = ("len", 0.9144, "yd"),
            ["mi"] = ("len", 1609.344, "mi"), ["mile"] = ("len", 1609.344, "mi"), ["miles"] = ("len", 1609.344, "mi"),
            // weight (base: gram)
            ["mg"] = ("mass", 0.001, "mg"), ["g"] = ("mass", 1, "g"), ["kg"] = ("mass", 1000, "kg"),
            ["oz"] = ("mass", 28.349523125, "oz"), ["lb"] = ("mass", 453.59237, "lb"), ["lbs"] = ("mass", 453.59237, "lb"),
            ["pound"] = ("mass", 453.59237, "lb"), ["pounds"] = ("mass", 453.59237, "lb"),
            // volume (base: litre)
            ["ml"] = ("vol", 0.001, "ml"), ["l"] = ("vol", 1, "l"), ["litre"] = ("vol", 1, "l"), ["liter"] = ("vol", 1, "l"),
            ["litres"] = ("vol", 1, "l"), ["liters"] = ("vol", 1, "l"), ["gal"] = ("vol", 3.785411784, "gal"),
            ["gallon"] = ("vol", 3.785411784, "gal"), ["gallons"] = ("vol", 3.785411784, "gal"), ["cup"] = ("vol", 0.2365882365, "cups"),
            ["cups"] = ("vol", 0.2365882365, "cups"),
            // data (base: byte)
            ["b"] = ("data", 1, "B"), ["kb"] = ("data", 1024, "KB"), ["mb"] = ("data", 1048576, "MB"),
            ["gb"] = ("data", 1073741824, "GB"), ["tb"] = ("data", 1099511627776, "TB"),
            // time (base: second)
            ["s"] = ("time", 1, "s"), ["sec"] = ("time", 1, "s"), ["min"] = ("time", 60, "min"), ["mins"] = ("time", 60, "min"),
            ["minutes"] = ("time", 60, "min"), ["h"] = ("time", 3600, "h"), ["hr"] = ("time", 3600, "h"), ["hours"] = ("time", 3600, "h"),
            ["day"] = ("time", 86400, "days"), ["days"] = ("time", 86400, "days"), ["week"] = ("time", 604800, "weeks"),
            ["weeks"] = ("time", 604800, "weeks"),
            // speed (base: m/s)
            ["kmh"] = ("speed", 1 / 3.6, "km/h"), ["km/h"] = ("speed", 1 / 3.6, "km/h"), ["mph"] = ("speed", 0.44704, "mph"),
            ["m/s"] = ("speed", 1, "m/s"),
        };

        private static string? TryConvert(string text)
        {
            int split = text.IndexOf(" in ", StringComparison.Ordinal);
            int len = 4;
            if (split < 0) { split = text.IndexOf(" to ", StringComparison.Ordinal); }
            if (split < 0) return null;

            string left = text[..split].Trim();
            string to = text[(split + len)..].Trim().TrimEnd('.');

            // Money: "50 usd in inr", "$20 to euros", "1000 rupees in dollars"
            string? money = TryCurrency(left, to);
            if (money != null) return money;

            // number (may be an expression) followed by a unit
            int unitStart = left.Length;
            while (unitStart > 0 && (char.IsLetter(left[unitStart - 1]) || left[unitStart - 1] == '/' || left[unitStart - 1] == '°')) unitStart--;
            string from = left[unitStart..].Trim().TrimStart('°');
            string numberText = left[..unitStart].Trim();
            if (from.Length == 0 || numberText.Length == 0) return null;
            double value = new Parser(numberText).Parse();
            to = to.TrimStart('°');

            // temperature is not a simple factor
            string? tf = TempUnit(from), tt = TempUnit(to);
            if (tf != null && tt != null)
            {
                double celsius = tf switch { "f" => (value - 32) * 5 / 9, "k" => value - 273.15, _ => value };
                double result = tt switch { "f" => celsius * 9 / 5 + 32, "k" => celsius + 273.15, _ => celsius };
                return Format(Math.Round(result, 2)) + (tt == "k" ? " K" : " °" + tt.ToUpperInvariant());
            }

            if (!Units.TryGetValue(from, out var a) || !Units.TryGetValue(to, out var b) || a.Group != b.Group) return null;
            return Format(Math.Round(value * a.Factor / b.Factor, 4)) + " " + b.Label;
        }

        private static readonly Dictionary<string, string> CurrencyWords = new()
        {
            ["$"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD", ["€"] = "EUR", ["euro"] = "EUR", ["euros"] = "EUR",
            ["£"] = "GBP", ["₹"] = "INR", ["rs"] = "INR", ["rupee"] = "INR", ["rupees"] = "INR", ["¥"] = "JPY", ["yen"] = "JPY",
            ["taka"] = "BDT", ["dirham"] = "AED", ["dirhams"] = "AED", ["riyal"] = "SAR",
        };

        private static string? CurrencyCode(string word)
        {
            word = word.Trim().ToLowerInvariant();
            if (CurrencyWords.TryGetValue(word, out var code)) return code;
            if (word.Length == 3 && word.All(char.IsLetter) && !Units.ContainsKey(word)) return word.ToUpperInvariant();
            return null;
        }

        private static string? TryCurrency(string left, string to)
        {
            string? toCode = CurrencyCode(to);
            if (toCode == null) return null;

            string? fromCode = null;
            string number = left;
            if (left.Length > 1 && CurrencyWords.TryGetValue(left[..1], out var prefixed))
            {
                fromCode = prefixed;
                number = left[1..];
            }
            else
            {
                int sp = left.LastIndexOf(' ');
                int i = left.Length;
                while (i > 0 && (char.IsLetter(left[i - 1]) || "$€£₹¥".Contains(left[i - 1]))) i--;
                if (i < left.Length)
                {
                    fromCode = CurrencyCode(left[i..]);
                    number = left[..i];
                }
                _ = sp;
            }
            if (fromCode == null) return null;

            double amount = new Parser(number.Trim()).Parse();
            double? a = Crest.Services.CurrencyService.Rate(fromCode), b = Crest.Services.CurrencyService.Rate(toCode);
            if (a == null || b == null)
                return Crest.Services.CurrencyService.Loaded ? null : "getting today's rates…";
            double result = amount / a.Value * b.Value;
            return Math.Round(result, 2).ToString("#,0.##", CultureInfo.InvariantCulture) + " " + toCode;
        }

        private static string? TempUnit(string u) => u switch
        {
            "c" or "celsius" => "c",
            "f" or "fahrenheit" => "f",
            "k" or "kelvin" => "k",
            _ => null
        };

        // ------------------------------------------------------------------ expression parser

        /// <summary>Recursive descent: + − × ÷ % ^, brackets, sqrt / abs / round / sin / cos / tan / log / ln, pi, e.</summary>
        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s) => _s = s.Replace(" ", "");

            public double Parse()
            {
                double v = Expr();
                if (_i != _s.Length) throw new FormatException();
                return v;
            }

            private char Peek => _i < _s.Length ? _s[_i] : '\0';

            private double Expr()
            {
                double v = Term();
                while (Peek is '+' or '-')
                {
                    char op = _s[_i++];
                    double r = Term();
                    v = op == '+' ? v + r : v - r;
                }
                return v;
            }

            private double Term()
            {
                double v = Power();
                while (Peek is '*' or '/' or '%')
                {
                    char op = _s[_i++];
                    if (op == '%' && (Peek == '\0' || Peek is '+' or '-' or ')' or '*' or '/'))
                    {
                        v /= 100;   // "50%" → 0.5
                        continue;
                    }
                    double r = Power();
                    v = op == '*' ? v * r : op == '/' ? v / r : v % r;
                }
                return v;
            }

            private double Power()
            {
                double b = Unary();
                if (Peek == '^')
                {
                    _i++;
                    return Math.Pow(b, Power());   // right-associative
                }
                return b;
            }

            private double Unary()
            {
                if (Peek == '-') { _i++; return -Unary(); }
                if (Peek == '+') { _i++; return Unary(); }
                return Primary();
            }

            private double Primary()
            {
                if (Peek == '(')
                {
                    _i++;
                    double v = Expr();
                    if (Peek != ')') throw new FormatException();
                    _i++;
                    return v;
                }

                if (char.IsLetter(Peek))
                {
                    int start = _i;
                    while (char.IsLetter(Peek)) _i++;
                    string name = _s[start.._i];
                    if (name == "pi") return Math.PI;
                    if (name == "e") return Math.E;
                    if (Peek != '(') throw new FormatException();
                    _i++;
                    double arg = Expr();
                    if (Peek != ')') throw new FormatException();
                    _i++;
                    return name switch
                    {
                        "sqrt" => Math.Sqrt(arg),
                        "abs" => Math.Abs(arg),
                        "round" => Math.Round(arg),
                        "sin" => Math.Sin(arg * Math.PI / 180),
                        "cos" => Math.Cos(arg * Math.PI / 180),
                        "tan" => Math.Tan(arg * Math.PI / 180),
                        "log" => Math.Log10(arg),
                        "ln" => Math.Log(arg),
                        _ => throw new FormatException()
                    };
                }

                int numStart = _i;
                while (char.IsDigit(Peek) || Peek == '.') _i++;
                if (numStart == _i) throw new FormatException();
                return double.Parse(_s[numStart.._i], CultureInfo.InvariantCulture);
            }
        }
    }
}
