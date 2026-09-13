using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Spc.Plugins
{
    /// <summary>Minimal JSON parser/writer. Sandbox safe, no external dependencies.</summary>
    internal static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = ParseValue(s, ref i);
            return v;
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && s.Substring(i, 4) == "null") { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                Ws(s, ref i);
                var key = ParseString(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                d[key] = ParseValue(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (i < s.Length)
            {
                l.Add(ParseValue(s, ref i));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return l;
        }

        private static string ParseString(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '/': sb.Append('/'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case 'u':
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.'
                                    || s[i] == 'e' || s[i] == 'E')) i++;
            var txt = s.Substring(start, i - start);
            double d;
            if (double.TryParse(txt, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        // ---------- accessors ----------

        public static Dictionary<string, object> Obj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> Arr(object o)
        {
            return o as List<object> ?? new List<object>();
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string key, string dflt = null)
        {
            var v = Get(d, key);
            if (v == null) return dflt;
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is double) return ((double)v).ToString("0.####", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        public static int? Int(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v == null) return null;
            if (v is double) return (int)Math.Round((double)v);
            int i;
            if (v is string && int.TryParse((string)v, NumberStyles.Any, CultureInfo.InvariantCulture, out i)) return i;
            return null;
        }

        public static bool? Bool(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            if (v == null) return null;
            if (v is bool) return (bool)v;
            var s = v as string;
            if (s != null) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
            if (v is double) return ((double)v) != 0;
            return null;
        }

        // ---------- writer ----------

        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Q(string s)
        {
            return "\"" + Escape(s) + "\"";
        }
    }
}
