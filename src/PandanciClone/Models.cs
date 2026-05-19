using System;
using System.Globalization;

namespace PandanciClone
{
    internal sealed class WordCard
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public string Word = "";
        public DateTime LastReview = DateTime.MinValue;
        public DateTime NextReview = DateTime.MinValue;
        public int Score = 100;
        public int Level = 3;
        public bool Flag1;
        public bool Flag2;

        public bool Due
        {
            get { return !Flag1 && NextReview != DateTime.MinValue && NextReview <= DateTime.Now; }
        }

        public static WordCard Parse(string line)
        {
            string[] p = line.Split('|');
            if (p.Length < 12 || p[0] != "WLBWordCard") return null;
            WordCard c = new WordCard();
            c.Y = ToInt(p[1], 0);
            c.X = ToInt(p[2], 0);
            c.Width = Math.Max(30, ToInt(p[3], 80));
            c.Height = Math.Max(20, ToInt(p[4], 28));
            c.Word = p[5];
            c.LastReview = ToDate(p[6]);
            c.NextReview = ToDate(p[7]);
            c.Score = ToInt(p[8], 100);
            c.Level = ToInt(p[9], 3);
            c.Flag1 = ToBool(p[10]);
            c.Flag2 = ToBool(p[11]);
            return c;
        }

        public string ToLine()
        {
            return string.Join("|", new string[]
            {
                "WLBWordCard",
                Y.ToString(CultureInfo.InvariantCulture),
                X.ToString(CultureInfo.InvariantCulture),
                Width.ToString(CultureInfo.InvariantCulture),
                Height.ToString(CultureInfo.InvariantCulture),
                Word,
                FormatDate(LastReview),
                FormatDate(NextReview),
                Score.ToString(CultureInfo.InvariantCulture),
                Level.ToString(CultureInfo.InvariantCulture),
                Flag1 ? "True" : "False",
                Flag2 ? "True" : "False"
            });
        }

        public void MarkReviewed(bool remembered)
        {
            bool firstReview = LastReview == DateTime.MinValue;
            LastReview = DateTime.Now;
            if (remembered)
            {
                Level = firstReview ? 1 : Math.Min(8, Level + 1);
                Score = 100;
            }
            else
            {
                Level = 0;
                Score = Math.Max(0, Score - 20);
            }

            double[] hours = new double[] { 3, 3, 8, 24, 48, 96, 168, 336, 720 };
            int index = Math.Max(0, Math.Min(Level, hours.Length - 1));
            NextReview = DateTime.Now.AddHours(hours[index]);
        }

        private static int ToInt(string text, int fallback)
        {
            int v;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static bool ToBool(string text)
        {
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime ToDate(string text)
        {
            DateTime d;
            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
            return DateTime.MinValue;
        }

        private static string FormatDate(DateTime date)
        {
            if (date == DateTime.MinValue) return "0001/1/1 0:00:00";
            return date.ToString("yyyy/M/d H:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class TextNote
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public string Text = "";

        public static TextNote Parse(string line)
        {
            string[] p = line.Split(new char[] { '|' }, 6);
            if (p.Length < 6 || p[0] != "WLBTextNotes") return null;
            TextNote n = new TextNote();
            n.Y = SafeInt(p[1], 0);
            n.X = SafeInt(p[2], 0);
            n.Width = Math.Max(60, SafeInt(p[3], 160));
            n.Height = Math.Max(30, SafeInt(p[4], 40));
            n.Text = p[5];
            return n;
        }

        public string ToLine()
        {
            return string.Join("|", new string[] { "WLBTextNotes", Y.ToString(), X.ToString(), Width.ToString(), Height.ToString(), Text.Replace("\r", " ").Replace("\n", " ") });
        }

        private static int SafeInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }
    }

    internal sealed class RawItem
    {
        public string Line;
        public RawItem(string line) { Line = line; }
    }

    internal sealed class ArrowItem
    {
        public int X1;
        public int Y1;
        public int X2;
        public int Y2;
        public int A;
        public int B;
        public int C;
        public int D;

        public static ArrowItem Parse(string line)
        {
            string[] p = line.Split('|');
            if (p.Length < 9 || p[0] != "WLBArrow") return null;
            ArrowItem item = new ArrowItem();
            item.X1 = SafeInt(p[1], 0);
            item.X2 = SafeInt(p[2], 0);
            item.Y1 = SafeInt(p[3], 0);
            item.Y2 = SafeInt(p[4], 0);
            item.A = SafeInt(p[5], 0);
            item.B = SafeInt(p[6], 0);
            item.C = SafeInt(p[7], 18);
            item.D = SafeInt(p[8], 15);
            return item;
        }

        public string ToLine()
        {
            return string.Join("|", new string[]
            {
                "WLBArrow", X1.ToString(), X2.ToString(), Y1.ToString(), Y2.ToString(),
                A.ToString(), B.ToString(), C.ToString(), D.ToString()
            });
        }

        private static int SafeInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }
    }

}
