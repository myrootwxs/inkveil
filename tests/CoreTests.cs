using System;
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
