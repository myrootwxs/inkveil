using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Inkveil
{
    public sealed class NovelChapter
    {
        public NovelChapter(string title, string text)
        {
            Title = title;
            Text = text;
        }

        public string Title { get; private set; }
        public string Text { get; private set; }
    }

    public sealed class NovelBook
    {
        private static readonly Regex ChineseHeading = new Regex(
            @"^(第[0-9〇零一二三四五六七八九十百千万两]+[章回节卷部篇集幕](?:[\s　:：·\-—].*)?)$",
            RegexOptions.Compiled);

        private static readonly Regex SpecialHeading = new Regex(
            @"^(序章|楔子|引子|前言|序言|后记|尾声|终章|完本感言|番外(?:篇)?|外传)(?:[\s　:：·\-—].*)?$",
            RegexOptions.Compiled);

        private static readonly Regex EnglishHeading = new Regex(
            @"^chapter\s+[0-9ivxlcdm]+(?:\s*[:：.\-—].*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private NovelBook(string title, string filePath, List<NovelChapter> chapters)
        {
            Title = title;
            FilePath = filePath;
            Chapters = chapters;
        }

        public string Title { get; private set; }
        public string FilePath { get; private set; }
        public List<NovelChapter> Chapters { get; private set; }

        public static NovelBook Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("文件路径不能为空。", "filePath");
            }

            byte[] data = File.ReadAllBytes(filePath);
            string text = DecodeText(data);
            return ParseText(text, Path.GetFileNameWithoutExtension(filePath), filePath);
        }

        public static string DecodeText(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            if (data.Length == 0)
            {
                return string.Empty;
            }

            Encoding encoding = DetectEncoding(data);
            int offset = GetBomLength(data);
            return encoding.GetString(data, offset, data.Length - offset).TrimStart('\uFEFF');
        }

        public static NovelBook ParseText(string content, string title, string filePath)
        {
            if (content == null)
            {
                content = string.Empty;
            }

            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var headings = new List<int>();

            for (int i = 0; i < lines.Length; i++)
            {
                string candidate = lines[i].Trim();
                if (IsHeading(candidate))
                {
                    headings.Add(i);
                }
            }

            var chapters = new List<NovelChapter>();
            if (headings.Count == 0)
            {
                string allText = NormalizeChapterText(string.Join("\n", lines));
                chapters.Add(new NovelChapter(title, allText));
                return new NovelBook(title, filePath, chapters);
            }

            if (headings[0] > 0)
            {
                string preface = NormalizeChapterText(string.Join("\n", lines, 0, headings[0]));
                if (!string.IsNullOrEmpty(preface))
                {
                    chapters.Add(new NovelChapter("开篇", preface));
                }
            }

            for (int i = 0; i < headings.Count; i++)
            {
                int start = headings[i];
                int end = i + 1 < headings.Count ? headings[i + 1] : lines.Length;
                string chapterTitle = lines[start].Trim();
                string body = start + 1 < end
                    ? NormalizeChapterText(string.Join("\n", lines, start + 1, end - start - 1))
                    : string.Empty;

                chapters.Add(new NovelChapter(chapterTitle, body));
            }

            if (chapters.Count == 0)
            {
                chapters.Add(new NovelChapter(title, string.Empty));
            }

            return new NovelBook(title, filePath, chapters);
        }

        private static bool IsHeading(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length > 60)
            {
                return false;
            }

            return ChineseHeading.IsMatch(line)
                || SpecialHeading.IsMatch(line)
                || EnglishHeading.IsMatch(line);
        }

        private static string NormalizeChapterText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var result = new StringBuilder(text.Length);

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    if (result.Length > 0 && result[result.Length - 1] != '\n')
                    {
                        result.Append('\n');
                    }
                    continue;
                }

                if (result.Length > 0 && result[result.Length - 1] != '\n')
                {
                    result.Append('\n');
                }
                result.Append(line);
            }

            return result.ToString().Trim('\n');
        }

        private static Encoding DetectEncoding(byte[] data)
        {
            if (data.Length >= 4)
            {
                if (data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
                {
                    return new UTF32Encoding(false, true);
                }
                if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF)
                {
                    return new UTF32Encoding(true, true);
                }
            }

            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                return new UTF8Encoding(true, true);
            }
            if (data.Length >= 2)
            {
                if (data[0] == 0xFF && data[1] == 0xFE)
                {
                    return new UnicodeEncoding(false, true, true);
                }
                if (data[0] == 0xFE && data[1] == 0xFF)
                {
                    return new UnicodeEncoding(true, true, true);
                }

                // 无 BOM 的 UTF-16 通常会出现大量交替的空字节。
                if (data[0] != 0 && data[1] == 0)
                {
                    return new UnicodeEncoding(false, false, true);
                }
                if (data[0] == 0 && data[1] != 0)
                {
                    return new UnicodeEncoding(true, false, true);
                }
            }

            try
            {
                var utf8 = new UTF8Encoding(false, true);
                utf8.GetString(data);
                return utf8;
            }
            catch (DecoderFallbackException)
            {
                return GetLegacyChineseEncoding();
            }
        }

        private static Encoding GetLegacyChineseEncoding()
        {
            try
            {
                return Encoding.GetEncoding(54936);
            }
            catch (ArgumentException)
            {
                return Encoding.Default;
            }
        }

        private static int GetBomLength(byte[] data)
        {
            if (data.Length >= 4
                && ((data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
                    || (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF)))
            {
                return 4;
            }
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                return 3;
            }
            if (data.Length >= 2
                && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
            {
                return 2;
            }
            return 0;
        }
    }
}
