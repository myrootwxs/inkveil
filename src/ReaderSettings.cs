using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Inkveil
{
    [DataContract]
    public sealed class ReaderSettings
    {
        public ReaderSettings()
        {
            FontSize = 21;
            FontFamilyName = "Microsoft YaHei UI";
            TextTheme = "light";
            BackgroundOpacity = 0;
            TextOpacity = 100;
            Topmost = true;
            AutoHide = true;
            Width = 880;
            Height = 700;
        }

        [DataMember] public string LastBookPath { get; set; }
        [DataMember] public int ChapterIndex { get; set; }
        [DataMember] public double FontSize { get; set; }
        [DataMember] public string FontFamilyName { get; set; }
        [DataMember] public string TextTheme { get; set; }
        [DataMember] public double BackgroundOpacity { get; set; }
        [DataMember] public double TextOpacity { get; set; }
        [DataMember] public bool Topmost { get; set; }
        [DataMember] public bool AutoHide { get; set; }
        [DataMember] public bool HasWindowBounds { get; set; }
        [DataMember] public double Left { get; set; }
        [DataMember] public double Top { get; set; }
        [DataMember] public double Width { get; set; }
        [DataMember] public double Height { get; set; }

        public static ReaderSettings Load()
        {
            string path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new ReaderSettings();
            }

            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ReaderSettings));
                    var settings = serializer.ReadObject(stream) as ReaderSettings;
                    return settings ?? new ReaderSettings();
                }
            }
            catch
            {
                return new ReaderSettings();
            }
        }

        public void Save()
        {
            string path = GetSettingsPath();
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                using (FileStream stream = File.Create(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ReaderSettings));
                    serializer.WriteObject(stream, this);
                }
            }
            catch
            {
                // 设置保存失败不应影响阅读。
            }
        }

        private static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Inkveil", "settings.json");
        }
    }
}
