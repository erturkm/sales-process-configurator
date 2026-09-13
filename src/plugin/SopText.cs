using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;

namespace Spc.Plugins
{
    /// <summary>
    /// Extracts plain text from an uploaded standard operating procedure so the Process Copilot can
    /// read it. Everything runs in memory because the plugin sandbox has no file system.
    /// Supported: .docx (Open XML), .pdf (text based, FlateDecode), .txt / .md / .csv, .htm / .html.
    /// Scanned PDFs contain images rather than text and cannot be read without OCR.
    /// </summary>
    internal static class SopText
    {
        public static string Extract(byte[] bytes, string fileName, out string kind)
        {
            kind = "unknown";
            if (bytes == null || bytes.Length == 0)
                throw new InvalidPluginExecutionException("The uploaded document is empty.");

            var ext = (Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant();

            // Sniff the magic number rather than trusting the extension.
            var isZip = bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B;
            var isPdf = bytes.Length > 4 && bytes[0] == 0x25 && bytes[1] == 0x50
                        && bytes[2] == 0x44 && bytes[3] == 0x46;

            if (isZip && (ext == ".docx" || ext == "" || ext == ".doc"))
            {
                kind = "docx";
                return Docx(bytes);
            }
            if (isPdf)
            {
                kind = "pdf";
                return Pdf(bytes);
            }
            if (ext == ".doc")
                throw new InvalidPluginExecutionException(
                    "Legacy .doc files are not supported. Save the document as .docx and upload it again.");

            kind = "text";
            var text = Decode(bytes);
            if (ext == ".htm" || ext == ".html" || Looks(text, "<html") || Looks(text, "<body"))
            {
                kind = "html";
                text = StripHtml(text);
            }
            return text;
        }

        private static bool Looks(string s, string token)
        {
            return s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------- docx ----------------

        private static string Docx(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var sb = new StringBuilder();

                // The body, then any headers that carry policy references, in document order.
                AppendPart(zip, "word/document.xml", sb);
                if (sb.Length == 0)
                {
                    foreach (var e in zip.Entries)
                        if (e.FullName.StartsWith("word/document", StringComparison.OrdinalIgnoreCase))
                            AppendPart(zip, e.FullName, sb);
                }

                if (sb.Length == 0)
                    throw new InvalidPluginExecutionException(
                        "That .docx file has no readable body text.");
                return Tidy(sb.ToString());
            }
        }

        private static void AppendPart(ZipArchive zip, string name, StringBuilder sb)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return;
            string xml;
            using (var s = entry.Open())
            using (var r = new StreamReader(s, Encoding.UTF8))
                xml = r.ReadToEnd();
            sb.Append(WordXmlToText(xml));
        }

        /// <summary>
        /// Turns WordprocessingML into text. Paragraph and row boundaries become newlines, cell
        /// boundaries become tabs, and numbering markers are preserved so step order survives.
        /// </summary>
        internal static string WordXmlToText(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return "";

            // Drop parts that are not body copy before touching anything else.
            xml = Regex.Replace(xml, @"<w:instrText[\s\S]*?</w:instrText>", " ");
            xml = Regex.Replace(xml, @"<w:delText[\s\S]*?</w:delText>", " ");

            var sb = new StringBuilder(xml.Length / 2);
            // Walk tags and text in order so structure is not lost.
            foreach (Match m in Regex.Matches(xml, @"<[^>]+>|[^<]+"))
            {
                var tok = m.Value;
                if (tok.Length == 0) continue;
                if (tok[0] != '<') { sb.Append(Unescape(tok)); continue; }

                if (StartsTag(tok, "w:tab")) sb.Append('\t');
                else if (StartsTag(tok, "w:br") || StartsTag(tok, "w:cr")) sb.Append('\n');
                else if (tok.StartsWith("</w:p", StringComparison.Ordinal)) sb.Append('\n');
                else if (tok.StartsWith("</w:tr", StringComparison.Ordinal)) sb.Append('\n');
                else if (tok.StartsWith("</w:tc", StringComparison.Ordinal)) sb.Append('\t');
            }
            return sb.ToString();
        }

        private static bool StartsTag(string tok, string name)
        {
            return tok.StartsWith("<" + name, StringComparison.Ordinal)
                   && (tok.Length == name.Length + 1
                       || tok[name.Length + 1] == ' '
                       || tok[name.Length + 1] == '/'
                       || tok[name.Length + 1] == '>');
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            return s.Replace("&lt;", "<").Replace("&gt;", ">")
                    .Replace("&quot;", "\"").Replace("&apos;", "'")
                    .Replace("&#160;", " ").Replace("&nbsp;", " ")
                    .Replace("&amp;", "&");
        }

        // ---------------- pdf ----------------

        /// <summary>
        /// Pulls text out of a digitally generated PDF: inflate each FlateDecode stream, then read
        /// the Tj / TJ / ' / " text showing operators. Scanned PDFs yield nothing and say so.
        /// </summary>
        private static string Pdf(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (var chunk in RawStreams(bytes))
            {
                var data = TryInflate(chunk) ?? chunk;
                var content = Latin1(data);
                if (content.IndexOf("Tj", StringComparison.Ordinal) < 0
                    && content.IndexOf("TJ", StringComparison.Ordinal) < 0) continue;
                AppendPdfText(content, sb);
            }

            var text = Tidy(sb.ToString());
            if (text.Trim().Length < 40)
                throw new InvalidPluginExecutionException(
                    "No readable text was found in that PDF. It is most likely a scan or an image, "
                    + "which needs OCR. Upload the Word version of the procedure instead.");
            return text;
        }

        private static IEnumerable<byte[]> RawStreams(byte[] bytes)
        {
            var marker = Encoding.ASCII.GetBytes("stream");
            var endMarker = Encoding.ASCII.GetBytes("endstream");
            var i = 0;
            while (i < bytes.Length)
            {
                var s = IndexOf(bytes, marker, i);
                if (s < 0) yield break;
                var p = s + marker.Length;
                if (p < bytes.Length && bytes[p] == (byte)'\r') p++;
                if (p < bytes.Length && bytes[p] == (byte)'\n') p++;
                var e = IndexOf(bytes, endMarker, p);
                if (e < 0) yield break;
                var len = e - p;
                if (len > 0 && len < 12 * 1024 * 1024)
                {
                    var buf = new byte[len];
                    Buffer.BlockCopy(bytes, p, buf, 0, len);
                    yield return buf;
                }
                i = e + endMarker.Length;
            }
        }

        private static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            var last = hay.Length - needle.Length;
            for (var i = start; i <= last; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        private static byte[] TryInflate(byte[] data)
        {
            // zlib header (0x78 ...) needs the two byte prefix skipped before raw deflate.
            var offsets = data.Length > 2 && data[0] == 0x78 ? new[] { 2, 0 } : new[] { 0, 2 };
            foreach (var off in offsets)
            {
                if (off >= data.Length) continue;
                try
                {
                    using (var src = new MemoryStream(data, off, data.Length - off))
                    using (var ds = new DeflateStream(src, CompressionMode.Decompress))
                    using (var outMs = new MemoryStream())
                    {
                        ds.CopyTo(outMs);
                        if (outMs.Length > 0) return outMs.ToArray();
                    }
                }
                catch { /* not deflate at this offset, try the next */ }
            }
            return null;
        }

        private static void AppendPdfText(string content, StringBuilder sb)
        {
            // TJ arrays: [(a) -20 (b)] TJ   and simple strings: (text) Tj
            foreach (Match m in Regex.Matches(content,
                         @"\[(?<arr>[\s\S]*?)\]\s*TJ|\((?<s>(?:\\.|[^\\()])*)\)\s*(?:Tj|')"))
            {
                if (m.Groups["arr"].Success)
                {
                    foreach (Match p in Regex.Matches(m.Groups["arr"].Value,
                                 @"\((?<s>(?:\\.|[^\\()])*)\)|(?<n>-?\d+(?:\.\d+)?)"))
                    {
                        if (p.Groups["s"].Success) sb.Append(PdfString(p.Groups["s"].Value));
                        else
                        {
                            // A large negative kern is a word gap in most producers.
                            double n;
                            if (double.TryParse(p.Groups["n"].Value,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out n)
                                && n < -100) sb.Append(' ');
                        }
                    }
                }
                else sb.Append(PdfString(m.Groups["s"].Value));
                sb.Append('\n');
            }
        }

        private static string PdfString(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= s.Length) break;
                var n = s[i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': case 'f': sb.Append(' '); break;
                    case '(': case ')': case '\\': sb.Append(n); break;
                    default:
                        if (n >= '0' && n <= '7')
                        {
                            var oct = "";
                            var j = i;
                            while (j < s.Length && oct.Length < 3 && s[j] >= '0' && s[j] <= '7')
                            { oct += s[j]; j++; }
                            i = j - 1;
                            try { sb.Append((char)Convert.ToInt32(oct, 8)); } catch { }
                        }
                        else sb.Append(n);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Latin1(byte[] b)
        {
            var sb = new StringBuilder(b.Length);
            foreach (var x in b) sb.Append((char)x);
            return sb.ToString();
        }

        // ---------------- shared ----------------

        private static string Decode(byte[] b)
        {
            if (b.Length > 2 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length > 1 && b[0] == 0xFF && b[1] == 0xFE)
                return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length > 1 && b[0] == 0xFE && b[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            return new UTF8Encoding(false).GetString(b);
        }

        private static string StripHtml(string s)
        {
            s = Regex.Replace(s, @"<(script|style)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(br|/p|/div|/li|/tr|/h\d)\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            return Tidy(Unescape(s));
        }

        /// <summary>Collapses runaway whitespace without destroying paragraph or list structure.</summary>
        internal static string Tidy(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');
            s = Regex.Replace(s, @"[ \t]+", " ");
            s = Regex.Replace(s, @" *\n *", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>
        /// Trims the document to fit the model budget, keeping the opening (scope and definitions)
        /// and the closing (approval and escalation rules), which is where procedure detail lives.
        /// </summary>
        internal static string Budget(string text, int maxChars, out bool trimmed)
        {
            trimmed = false;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
            trimmed = true;
            var head = (int)(maxChars * 0.7);
            var tail = maxChars - head - 80;
            if (tail < 0) { tail = 0; head = maxChars - 80; }
            return text.Substring(0, head)
                   + "\n\n[... middle of the document omitted to fit the model context ...]\n\n"
                   + text.Substring(text.Length - tail);
        }
    }
}
