// JSON lu et réécrit tel quel, champs inconnus compris (module 13c, étape 3).
// DataContractJsonSerializer ne garde que les champs déclarés : renvoyer au serveur les
// réglages du compte (/Users/Configuration, objet entier) en effacerait. Ici, un objet
// devient un dictionnaire (ordre gardé), un tableau une liste, un nombre son texte
// d'origine (JsonTree.Number), true/false un bool, null null.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NativeAIO.Core
{
    static class JsonTree
    {
        // Nombre gardé sous sa forme écrite : rien n'est arrondi ni reformaté.
        public sealed class Number
        {
            public readonly string Text;
            public Number(string text) { Text = text; }
            public override string ToString() => Text;
        }

        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Space(s, ref i);
            if (i != s.Length) throw new FormatException("JSON : texte en trop à " + i);
            return v;
        }

        public static object Parse(byte[] data) => Parse(Encoding.UTF8.GetString(data));

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        public static byte[] Bytes(object v) => Encoding.UTF8.GetBytes(Write(v));

        // ---- Lecture ----

        static void Space(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object Value(string s, ref int i)
        {
            Space(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON : fin inattendue");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("JSON : caractère inattendu à " + i);
            return new Number(s.Substring(start, i - start));
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            Space(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Space(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("JSON : nom attendu à " + i);
                string k = Str(s, ref i);
                Space(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON : « : » attendu à " + i);
                i++;
                d[k] = Value(s, ref i);
                Space(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("JSON : « , » ou « } » attendu à " + i);
            }
        }

        static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            Space(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Space(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return l; }
                throw new FormatException("JSON : « , » ou « ] » attendu à " + i);
            }
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // "
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON : \\u incomplet");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            throw new FormatException("JSON : texte non fermé");
        }

        // ---- Écriture ----

        static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string str: Quote(sb, str); break;
                case Number n: sb.Append(n.Text); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case Dictionary<string, object> d:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in d)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Quote(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case List<object> l:
                    sb.Append('[');
                    for (int k = 0; k < l.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        Write(sb, l[k]);
                    }
                    sb.Append(']');
                    break;
                default: throw new ArgumentException("JSON : type non pris en charge " + v.GetType().Name);
            }
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
