using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Text;
using Inkveil;

internal static class CoreTests
{
    private static int _passed;

    private static void Main()
    {
        TestChapterParsing();
        TestBookWithoutHeadings();
        TestUtf8BomDecoding();
        TestGb18030Decoding();
        TestEpub3Parsing();
        TestEpub2NcxParsingAndEncoding();
        TestReaderSettingsRoundTrip();

        Console.WriteLine("全部核心测试通过：" + _passed);
    }

    private static void TestChapterParsing()
    {
        string text =
            "这是卷首内容。\n\n" +
            "第一章 雨夜\n\n" +
            "雨落在旧城的屋檐上。\n\n" +
            "他翻开了那本书。\n\n" +
            "第二章 灯火\n\n" +
            "灯火从长街尽头亮起。";

        NovelBook book = NovelBook.ParseText(text, "测试小说", "test.txt");
        AssertEqual(3, book.Chapters.Count, "应识别卷首与两个章节");
        AssertEqual("开篇", book.Chapters[0].Title, "卷首标题");
        AssertEqual("第一章 雨夜", book.Chapters[1].Title, "第一章标题");
        AssertTrue(book.Chapters[1].Text.Contains("旧城的屋檐"), "第一章正文");
        AssertEqual("第二章 灯火", book.Chapters[2].Title, "第二章标题");
    }

    private static void TestBookWithoutHeadings()
    {
        NovelBook book = NovelBook.ParseText("只有一段正文。\n第二行。", "短篇", "short.txt");
        AssertEqual(1, book.Chapters.Count, "无章节标题时应为单章");
        AssertEqual("短篇", book.Chapters[0].Title, "单章标题使用文件名");
        AssertTrue(book.Chapters[0].Text.Contains("第二行"), "单章正文完整");
    }

    private static void TestUtf8BomDecoding()
    {
        byte[] payload = Encoding.UTF8.GetBytes("第一章 春风\n正文");
        byte[] data = new byte[payload.Length + 3];
        data[0] = 0xEF;
        data[1] = 0xBB;
        data[2] = 0xBF;
        Buffer.BlockCopy(payload, 0, data, 3, payload.Length);

        AssertEqual("第一章 春风\n正文", NovelBook.DecodeText(data), "UTF-8 BOM 解码");
    }

    private static void TestGb18030Decoding()
    {
        Encoding encoding = Encoding.GetEncoding(54936);
        byte[] data = encoding.GetBytes("第一章 旧梦\n风从北方来。");
        AssertEqual("第一章 旧梦\n风从北方来。", NovelBook.DecodeText(data), "GB18030 解码");
    }

    private static void TestEpub3Parsing()
    {
        string path = CreateTemporaryEpub(delegate(ZipArchive archive)
        {
            AddTextEntry(archive, "mimetype", "application/epub+zip");
            AddTextEntry(
                archive,
                "META-INF/container.xml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" " +
                "media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            AddTextEntry(
                archive,
                "OEBPS/content.opf",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\">" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                "<dc:title>测试 EPUB</dc:title></metadata>" +
                "<manifest>" +
                "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>" +
                "<item id=\"c1\" href=\"text/ch1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"c2\" href=\"text/ch2.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"pic\" href=\"images/pic.png\" media-type=\"image/png\"/>" +
                "</manifest><spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>");
            AddTextEntry(
                archive,
                "OEBPS/nav.xhtml",
                "<html xmlns=\"http://www.w3.org/1999/xhtml\" " +
                "xmlns:epub=\"http://www.idpf.org/2007/ops\"><body>" +
                "<nav epub:type=\"toc\"><ol>" +
                "<li><a href=\"text/ch1.xhtml#start\">第一章 雨</a></li>" +
                "<li><a href=\"text/ch2.xhtml\">第二章 灯</a></li>" +
                "</ol></nav></body></html>");
            AddTextEntry(
                archive,
                "OEBPS/text/ch1.xhtml",
                "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>" +
                "<h1 id=\"start\">第一章 雨</h1>" +
                "<p>雨落在<strong>旧城</strong>，也落在<em>屋檐</em>上。</p>" +
                "<img src=\"../images/pic.png\" alt=\"雨夜插图\"/>" +
                "</body></html>");
            AddTextEntry(
                archive,
                "OEBPS/text/ch2.xhtml",
                "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>" +
                "<h2>第二章 灯</h2><p style=\"text-align: center\">灯亮了&nbsp;很久。</p>" +
                "</body></html>");
            AddBytesEntry(
                archive,
                "OEBPS/images/pic.png",
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        });

        try
        {
            NovelBook book = NovelBook.Load(path);
            AssertEqual("测试 EPUB", book.Title, "EPUB 元数据标题");
            AssertEqual(2, book.Chapters.Count, "EPUB 目录章节数量");
            AssertEqual("第一章 雨", book.Chapters[0].Title, "EPUB3 目录标题");
            AssertTrue(book.Chapters[0].Text.Contains("旧城"), "EPUB 正文文本");

            bool foundImage = false;
            bool foundBold = false;
            bool foundItalic = false;
            foreach (NovelBlock block in book.Chapters[0].Blocks)
            {
                foreach (NovelInline inline in block.Inlines)
                {
                    foundImage = foundImage || inline.Kind == NovelInlineKind.Image;
                    foundBold = foundBold || inline.Bold;
                    foundItalic = foundItalic || inline.Italic;
                }
            }
            AssertTrue(foundImage, "EPUB 内嵌图片");
            AssertTrue(foundBold, "EPUB 粗体保留");
            AssertTrue(foundItalic, "EPUB 斜体保留");
            AssertEqual(NovelTextAlignment.Center, book.Chapters[1].Blocks[1].Alignment, "EPUB 居中排版");
            AssertTrue(book.Chapters[1].Text.Contains("灯亮了 很久"), "EPUB 实体解码");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void TestEpub2NcxParsingAndEncoding()
    {
        string path = CreateTemporaryEpub(delegate(ZipArchive archive)
        {
            AddTextEntry(archive, "mimetype", "application/epub+zip");
            AddTextEntry(
                archive,
                "META-INF/container.xml",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile full-path=\"OPS/book.opf\" " +
                "media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            AddTextEntry(
                archive,
                "OPS/book.opf",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\">" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
                "<dc:title>NCX 小说</dc:title></metadata><manifest>" +
                "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>" +
                "<item id=\"main\" href=\"main.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "</manifest><spine toc=\"ncx\"><itemref idref=\"main\"/></spine></package>");
            AddTextEntry(
                archive,
                "OPS/toc.ncx",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><navMap>" +
                "<navPoint id=\"p1\"><navLabel><text>第一卷</text></navLabel>" +
                "<content src=\"main.xhtml#one\"/>" +
                "<navPoint id=\"p2\"><navLabel><text>第一章</text></navLabel>" +
                "<content src=\"main.xhtml#two\"/></navPoint>" +
                "</navPoint></navMap></ncx>");

            Encoding encoding = Encoding.GetEncoding(54936);
            byte[] chapter = encoding.GetBytes(
                "<?xml version=\"1.0\" encoding=\"gb18030\"?>" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>" +
                "<h1 id=\"one\">第一卷</h1><p>山雨欲来。</p>" +
                "<h1 id=\"two\">第一章</h1><p>旧梦重逢。</p>" +
                "</body></html>");
            AddBytesEntry(archive, "OPS/main.xhtml", chapter);
        });

        try
        {
            NovelBook book = NovelBook.Load(path);
            AssertEqual(2, book.Chapters.Count, "EPUB2 NCX 章节拆分");
            AssertEqual("第一卷", book.Chapters[0].Title, "EPUB2 NCX 父级标题");
            AssertEqual(0, book.Chapters[0].TocDepth, "EPUB2 目录层级");
            AssertEqual(1, book.Chapters[1].TocDepth, "EPUB2 子目录层级");
            AssertTrue(book.Chapters[1].Text.Contains("旧梦重逢"), "EPUB 声明编码解码");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTemporaryEpub(Action<ZipArchive> populate)
    {
        string path = Path.Combine(Path.GetTempPath(), "inkveil-" + Guid.NewGuid().ToString("N") + ".epub");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            populate(archive);
        }
        return path;
    }

    private static void AddTextEntry(ZipArchive archive, string path, string text)
    {
        AddBytesEntry(archive, path, new UTF8Encoding(false).GetBytes(text));
    }

    private static void AddBytesEntry(ZipArchive archive, string path, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        {
            stream.Write(data, 0, data.Length);
        }
    }

    private static void TestReaderSettingsRoundTrip()
    {
        var settings = new ReaderSettings
        {
            LastBookPath = @"C:\Books\长夜.txt",
            ChapterIndex = 12,
            ChapterScrollRatio = 0.375
        };
        var serializer = new DataContractJsonSerializer(typeof(ReaderSettings));

        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, settings);
            stream.Position = 0;
            var restored = (ReaderSettings)serializer.ReadObject(stream);

            AssertEqual(settings.LastBookPath, restored.LastBookPath, "阅读文件持久化");
            AssertEqual(12, restored.ChapterIndex, "章节进度持久化");
            AssertEqual(0.375, restored.ChapterScrollRatio, "章节内进度持久化");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        if (!object.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                string.Format("{0}失败。期望：{1}，实际：{2}", name, expected, actual));
        }
        _passed++;
    }

    private static void AssertTrue(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name + "失败。");
        }
        _passed++;
    }
}
