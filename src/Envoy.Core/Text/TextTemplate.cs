using System.Globalization;
using System.Text;

namespace Envoy.Core.Text;

/// <summary>
/// The one formatter for every Core <c>*Text</c> template (contract §8.7). Templates use named placeholders
/// (<c>{count}</c>, never <c>{0}</c>) so they can move to <c>.resx</c> unchanged.
/// </summary>
public static class TextTemplate
{
    /// <summary>
    /// Replaces each <c>{name}</c> or <c>{name:format}</c> with the matching value, formatted with
    /// <paramref name="culture"/> (null = <see cref="CultureInfo.CurrentCulture"/>). <c>{{</c> and <c>}}</c> are
    /// literal braces. A null value is written as an empty string.
    /// </summary>
    /// <exception cref="FormatException">
    /// The template is malformed, names a placeholder with no value, or a value is never used by the template — so a
    /// template/argument mismatch fails its unit test.
    /// </exception>
    public static string Format(string template, IFormatProvider? culture, params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(template);
        culture ??= CultureInfo.CurrentCulture;

        var lookup = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrEmpty(name)) throw new FormatException("Template values need a name.");
            if (!lookup.TryAdd(name, value)) throw new FormatException($"Template value '{name}' is given twice.");
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i++; continue; }
                throw new FormatException($"Unmatched '}}' at {i} in template \"{template}\".");
            }
            if (c != '{')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }

            var close = template.IndexOf('}', i + 1);
            if (close < 0) throw new FormatException($"Unclosed '{{' at {i} in template \"{template}\".");
            var body = template.AsSpan(i + 1, close - i - 1);
            var colon = body.IndexOf(':');
            var name = (colon < 0 ? body : body[..colon]).ToString();
            var format = colon < 0 ? null : body[(colon + 1)..].ToString();
            if (name.Length == 0 || name.Contains('{'))
                throw new FormatException($"Malformed placeholder at {i} in template \"{template}\".");
            if (!lookup.TryGetValue(name, out var value))
                throw new FormatException($"Template \"{template}\" uses {{{name}}} but no value was given for it.");
            used.Add(name);
            sb.Append(value switch
            {
                null => "",
                IFormattable f => f.ToString(format, culture),
                _ => value.ToString(),
            });
            i = close;
        }

        foreach (var name in lookup.Keys)
        {
            if (!used.Contains(name))
                throw new FormatException($"Template \"{template}\" does not use the value '{name}'.");
        }
        return sb.ToString();
    }
}
