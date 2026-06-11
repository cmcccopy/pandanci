using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace PandanciClone
{
    internal sealed class OcrService
    {
        private readonly string _configuredTesseractPath;
        private readonly string _language;

        public OcrService(string configuredTesseractPath, string language)
        {
            _configuredTesseractPath = configuredTesseractPath == null ? "" : configuredTesseractPath.Trim();
            _language = NormalizeLanguage(language);
        }

        public string Recognize(Bitmap bitmap)
        {
            if (bitmap == null) throw new ArgumentNullException("bitmap");

            string tesseract = FindTesseract();
            string tessdataDir = FindTessdataDir(tesseract);
            string language = ResolveAvailableLanguage(tessdataDir);
            string imagePath = Path.Combine(Path.GetTempPath(), "pandanci_ocr_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                using (Bitmap prepared = PrepareForOcr(bitmap))
                {
                    prepared.Save(imagePath, ImageFormat.Png);
                }

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = tesseract;
                info.Arguments = Quote(imagePath) + " stdout --tessdata-dir " + Quote(tessdataDir) + " -l " + language + " --psm 6";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = Encoding.UTF8;
                info.StandardErrorEncoding = Encoding.UTF8;
                info.EnvironmentVariables["TESSDATA_PREFIX"] = tessdataDir;

                using (Process process = new Process())
                {
                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();
                    process.StartInfo = info;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) error.AppendLine(e.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(); }
                        catch { }
                        throw new InvalidOperationException("OCR 超时，请缩小截图区域后重试。");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException("Tesseract OCR 失败：" + CleanMessage(error.ToString()));
                    }

                    string text = CleanText(output.ToString());
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        throw new InvalidOperationException("OCR 没有识别到文字，请重新框选更清晰的区域。");
                    }
                    return text;
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(imagePath)) File.Delete(imagePath);
                }
                catch
                {
                }
            }
        }

        private string FindTesseract()
        {
            if (!string.IsNullOrWhiteSpace(_configuredTesseractPath))
            {
                if (Path.GetFileName(_configuredTesseractPath).Equals(_configuredTesseractPath, StringComparison.OrdinalIgnoreCase))
                {
                    return _configuredTesseractPath;
                }
                if (Directory.Exists(_configuredTesseractPath))
                {
                    string configuredDirPath = Path.Combine(_configuredTesseractPath, "tesseract.exe");
                    if (File.Exists(configuredDirPath)) return configuredDirPath;
                }
                if (File.Exists(_configuredTesseractPath)) return _configuredTesseractPath;
                throw new FileNotFoundException("未找到设置的 tesseract.exe：" + _configuredTesseractPath);
            }

            string envPath = Environment.GetEnvironmentVariable("TESSERACT_PATH");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath)) return envPath;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "tesseract.exe"),
                Path.Combine(baseDir, "tesseract", "tesseract.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tesseract.exe")
            };
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "tesseract.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }

            throw new FileNotFoundException("未找到 tesseract.exe。请安装 Tesseract OCR，或在 设置 -> OCR 设置 中填写 tesseract.exe 路径。");
        }

        private string FindTessdataDir(string tesseractPath)
        {
            string configuredDir = "";
            if (!string.IsNullOrWhiteSpace(_configuredTesseractPath))
            {
                configuredDir = Directory.Exists(_configuredTesseractPath)
                    ? _configuredTesseractPath
                    : Path.GetDirectoryName(_configuredTesseractPath);
            }

            string exeDir = Path.GetDirectoryName(tesseractPath);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string envPrefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? "";
            string[] candidates = new string[]
            {
                envPrefix,
                Path.Combine(envPrefix, "tessdata"),
                Path.Combine(configuredDir ?? "", "tessdata"),
                Path.Combine(configuredDir ?? "", "share", "tessdata"),
                Path.Combine(exeDir ?? "", "tessdata"),
                Path.Combine(exeDir ?? "", "share", "tessdata"),
                Path.Combine(exeDir ?? "", "..", "share", "tessdata"),
                Path.Combine(baseDir, "tessdata"),
                Path.Combine(baseDir, "tesseract", "tessdata"),
                Path.Combine(baseDir, "tesseract", "share", "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tessdata")
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (Directory.Exists(full) && HasAnyTrainedData(full)) return full;
                }
                catch
                {
                }
            }

            throw new DirectoryNotFoundException("未找到 Tesseract 语言包目录 tessdata。请把 eng.traineddata 和 chi_sim.traineddata 放到程序目录下的 tesseract\\tessdata 文件夹，或安装完整 Tesseract 后在 设置 -> OCR 设置 中填写安装目录。");
        }

        private string ResolveAvailableLanguage(string tessdataDir)
        {
            string[] languages = _language.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder missing = new StringBuilder();
            StringBuilder available = new StringBuilder();
            foreach (string language in languages)
            {
                string name = language.Trim();
                if (name.Length == 0) continue;
                string dataPath = Path.Combine(tessdataDir, name + ".traineddata");
                if (File.Exists(dataPath))
                {
                    if (available.Length > 0) available.Append("+");
                    available.Append(name);
                }
                else
                {
                    if (missing.Length > 0) missing.Append(", ");
                    missing.Append(name).Append(".traineddata");
                }
            }
            if (available.Length > 0) return available.ToString();

            string englishPath = Path.Combine(tessdataDir, "eng.traineddata");
            if (File.Exists(englishPath)) return "eng";

            string[] dataFiles = Directory.GetFiles(tessdataDir, "*.traineddata");
            if (dataFiles.Length > 0) return Path.GetFileNameWithoutExtension(dataFiles[0]);

            if (missing.Length > 0)
            {
                throw new FileNotFoundException("Tesseract 语言包缺失：" + missing + "。请放到 " + tessdataDir + "，或把 OCR 语言改成已安装的语言。");
            }
            throw new FileNotFoundException("Tesseract 语言包目录中没有 .traineddata 文件：" + tessdataDir);
        }

        private static bool HasAnyTrainedData(string tessdataDir)
        {
            try
            {
                return Directory.GetFiles(tessdataDir, "*.traineddata").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static Bitmap PrepareForOcr(Bitmap source)
        {
            int scale = source.Width < 1600 && source.Height < 1200 ? 2 : 1;
            Bitmap target = new Bitmap(Math.Max(1, source.Width * scale), Math.Max(1, source.Height * scale), PixelFormat.Format24bppRgb);
            target.SetResolution(300, 300);
            using (Graphics g = Graphics.FromImage(target))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height));
            }
            return target;
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return "eng+chi_sim";
            StringBuilder value = new StringBuilder();
            foreach (char c in language.Trim())
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '+' || c == '-')
                {
                    value.Append(c);
                }
            }
            return value.Length == 0 ? "eng+chi_sim" : value.ToString();
        }

        private static string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string value = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            while (value.IndexOf("\n\n\n", StringComparison.Ordinal) >= 0) value = value.Replace("\n\n\n", "\n\n");
            return value;
        }

        private static string CleanMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "未知错误。";
            return message.Trim();
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
