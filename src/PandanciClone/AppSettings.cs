using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace PandanciClone
{
    internal sealed class AppSettings
    {
        private readonly string _path;

        public string GoogleProxyAddress = "";
        public string TesseractPath = "";
        public string OcrLanguage = "eng+chi_sim";
        public bool HasPopupLocation;
        public Point PopupLocation;
        public bool HasPopupSize;
        public Size PopupSize;

        private AppSettings(string path)
        {
            _path = path;
        }

        public static AppSettings Load(string baseDir)
        {
            string path = Path.Combine(baseDir, "PandanciClone.settings");
            AppSettings settings = new AppSettings(path);
            if (!File.Exists(path)) return settings;

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (string line in lines)
            {
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (string.Equals(key, "GoogleProxyAddress", StringComparison.OrdinalIgnoreCase))
                {
                    settings.GoogleProxyAddress = value;
                }
                else if (string.Equals(key, "TesseractPath", StringComparison.OrdinalIgnoreCase))
                {
                    settings.TesseractPath = value;
                }
                else if (string.Equals(key, "OcrLanguage", StringComparison.OrdinalIgnoreCase))
                {
                    settings.OcrLanguage = string.IsNullOrWhiteSpace(value) ? "eng+chi_sim" : value;
                }
                else if (string.Equals(key, "PopupX", StringComparison.OrdinalIgnoreCase))
                {
                    int x;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out x))
                    {
                        settings.PopupLocation = new Point(x, settings.PopupLocation.Y);
                        settings.HasPopupLocation = true;
                    }
                }
                else if (string.Equals(key, "PopupY", StringComparison.OrdinalIgnoreCase))
                {
                    int y;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
                    {
                        settings.PopupLocation = new Point(settings.PopupLocation.X, y);
                        settings.HasPopupLocation = true;
                    }
                }
                else if (string.Equals(key, "PopupWidth", StringComparison.OrdinalIgnoreCase))
                {
                    int width;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) && width > 0)
                    {
                        settings.PopupSize = new Size(width, settings.PopupSize.Height);
                        settings.HasPopupSize = true;
                    }
                }
                else if (string.Equals(key, "PopupHeight", StringComparison.OrdinalIgnoreCase))
                {
                    int height;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height) && height > 0)
                    {
                        settings.PopupSize = new Size(settings.PopupSize.Width, height);
                        settings.HasPopupSize = true;
                    }
                }
            }
            return settings;
        }

        public void Save()
        {
            StringBuilder text = new StringBuilder();
            text.Append("GoogleProxyAddress=").AppendLine(GoogleProxyAddress ?? "");
            text.Append("TesseractPath=").AppendLine(TesseractPath ?? "");
            text.Append("OcrLanguage=").AppendLine(string.IsNullOrWhiteSpace(OcrLanguage) ? "eng+chi_sim" : OcrLanguage);
            if (HasPopupLocation)
            {
                text.Append("PopupX=").AppendLine(PopupLocation.X.ToString(CultureInfo.InvariantCulture));
                text.Append("PopupY=").AppendLine(PopupLocation.Y.ToString(CultureInfo.InvariantCulture));
            }
            if (HasPopupSize)
            {
                text.Append("PopupWidth=").AppendLine(PopupSize.Width.ToString(CultureInfo.InvariantCulture));
                text.Append("PopupHeight=").AppendLine(PopupSize.Height.ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(_path, text.ToString(), Encoding.UTF8);
        }
    }
}
