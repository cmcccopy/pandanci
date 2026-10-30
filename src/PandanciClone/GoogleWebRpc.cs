using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PandanciClone
{
    // Independent implementation of the Google Translate Web MkEWBc wire format.
    // Protocol references: UlionTse/translators (GoogleV2), ssut/py-googletrans.
    internal static class GoogleWebRpc
    {
        public static string BuildBody(string text, string targetLanguage)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string argument = serializer.Serialize(new object[] { new object[] { text, "auto", targetLanguage, true }, new object[] { 1 } });
            string envelope = serializer.Serialize(new object[] { new object[] { new object[] { "MkEWBc", argument, null, "generic" } } });
            return "f.req=" + Uri.EscapeDataString(envelope);
        }

        public static TranslationResult Parse(string response, string text, TranslationTarget target)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 4 * 1024 * 1024;
            // Responses start with an XSSI guard and contain length-prefixed JSON frames.
            using (StringReader reader = new StringReader(response ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (!line.StartsWith("[", StringComparison.Ordinal)) continue;
                    object[] frames;
                    try { frames = serializer.DeserializeObject(line) as object[]; }
                    catch (ArgumentException) { continue; }
                    if (frames == null) continue;
                    foreach (object item in frames)
                    {
                        object[] frame = item as object[];
                        if (frame == null || frame.Length < 3 || Convert.ToString(frame[0]) != "wrb.fr" || Convert.ToString(frame[1]) != "MkEWBc") continue;
                        object[] payload;
                        try { payload = serializer.DeserializeObject(Convert.ToString(frame[2])) as object[]; }
                        catch (ArgumentException) { continue; }
                        object[] translation = Element(payload, 1) as object[];
                        object[] sentences = Element(Element(Element(translation, 0) as object[], 0) as object[], 5) as object[];
                        StringBuilder translated = new StringBuilder();
                        if (sentences != null)
                        {
                            foreach (object sentence in sentences)
                            {
                                string part = Element(sentence as object[], 0) as string;
                                if (string.IsNullOrEmpty(part)) continue;
                                if (translated.Length > 0) translated.Append(' ');
                                translated.Append(part);
                            }
                        }
                        if (translated.Length == 0) continue;
                        return new TranslationResult {
                            SourceText = text, Provider = "Google", TranslatedText = translated.ToString(),
                            TargetLanguage = target.GoogleCode, DetectedLanguage = Element(payload, 2) as string ?? "",
                            DirectionText = target.DirectionText + " · Google Web RPC（免密钥）"
                        };
                    }
                }
            }
            throw new InvalidDataException("Google Web RPC 响应结构发生变化或没有返回译文。");
        }

        private static object Element(object[] values, int index)
        {
            return values != null && index >= 0 && index < values.Length ? values[index] : null;
        }
    }
}
