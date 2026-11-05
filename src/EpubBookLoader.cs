using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Inkveil
{
    public static class EpubBookLoader
    {
        private const string XLinkNamespace = "http://www.w3.org/1999/xlink";

        private static readonly Regex EncodingDeclaration = new Regex(
            @"(?:encoding|charset)\s*=\s*[""'](?<name>[^""']+)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedEntity = new Regex(
            @"&(?<name>[A-Za-z][A-Za-z0-9]+);",
            RegexOptions.Compiled);

        public static NovelBook Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("文件路径不能为空。", "filePath");
            }

            using (FileStream stream = File.OpenRead(filePath))
            using (var archive = new BookArchive(stream))
            {
                return Load(archive, Path.GetFileNameWithoutExtension(filePath), filePath);
            }
        }

        private static NovelBook Load(BookArchive archive, string fallbackTitle, string filePath)
        {
            // EPUB 的固定入口是容器文件，再由它定位实际的 OPF 包描述。
            byte[] containerBytes = archive.ReadRequired("META-INF/container.xml", "EPUB 缺少容器信息。");
            XmlDocument container = LoadXml(containerBytes, "META-INF/container.xml");
            XmlElement rootFile = FindFirstDescendant(container.DocumentElement, "rootfile");
            string packagePath = rootFile == null ? null : GetAttribute(rootFile, "full-path");
            if (string.IsNullOrEmpty(packagePath))
            {
                throw new InvalidDataException("EPUB 容器中未找到 OPF 文件路径。");
            }
            packagePath = NormalizeArchivePath(packagePath);

            XmlDocument package = LoadXml(
                archive.ReadRequired(packagePath, "EPUB 缺少 OPF 文件。"),
                packagePath);
            string title = ReadTitle(package, fallbackTitle);

            var manifest = new List<ManifestItem>();
            var manifestById = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
            XmlElement manifestElement = FindFirstDescendant(package.DocumentElement, "manifest");
            if (manifestElement != null)
            {
                foreach (XmlElement itemElement in ChildElements(manifestElement, "item"))
                {
                    string id = GetAttribute(itemElement, "id");
                    string href = GetAttribute(itemElement, "href");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(href))
                    {
                        continue;
                    }

                    var item = new ManifestItem
                    {
                        Id = id,
                        Path = ResolvePath(packagePath, href),
                        MediaType = GetAttribute(itemElement, "media-type"),
                        Properties = GetAttribute(itemElement, "properties")
                    };
                    manifest.Add(item);
                    if (!manifestById.ContainsKey(id))
                    {
                        manifestById.Add(id, item);
                    }
                }
            }

            var spineItems = new List<ManifestItem>();
            var seenSpinePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string ncxId = null;
            // spine 决定正文阅读顺序，目录只负责命名与章节切分。
            XmlElement spineElement = FindFirstDescendant(package.DocumentElement, "spine");
            if (spineElement != null)
            {
                ncxId = GetAttribute(spineElement, "toc");
                foreach (XmlElement itemRef in ChildElements(spineElement, "itemref"))
                {
                    string linear = GetAttribute(itemRef, "linear");
                    if (string.Equals(linear, "no", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string idRef = GetAttribute(itemRef, "idref");
                    ManifestItem item;
                    if (!string.IsNullOrEmpty(idRef)
                        && manifestById.TryGetValue(idRef, out item)
                        && IsContentItem(item)
                        && seenSpinePaths.Add(item.Path))
                    {
                        spineItems.Add(item);
                    }
                }
            }

            if (spineItems.Count == 0)
            {
                foreach (ManifestItem item in manifest)
                {
                    if (IsContentItem(item) && seenSpinePaths.Add(item.Path))
                    {
                        spineItems.Add(item);
                    }
                }
            }
            if (spineItems.Count == 0)
            {
                throw new InvalidDataException("EPUB 中没有可阅读的 XHTML 内容。");
            }

            List<TocEntry> toc = LoadTableOfContents(
                archive,
                manifest,
                manifestById,
                packagePath,
                ncxId);
            List<NovelChapter> chapters = BuildChapters(archive, spineItems, manifest, toc);
            if (chapters.Count == 0)
            {
                throw new InvalidDataException("EPUB 中没有可显示的章节。");
            }

            return new NovelBook(title, filePath, chapters);
        }

        private static string ReadTitle(XmlDocument package, string fallbackTitle)
        {
            XmlElement metadata = FindFirstDescendant(package.DocumentElement, "metadata");
            XmlElement titleElement = metadata == null
                ? null
                : FindFirstDescendant(metadata, "title");
            string title = titleElement == null ? null : CleanText(titleElement.InnerText);
            return string.IsNullOrEmpty(title) ? fallbackTitle : title;
        }

        private static List<TocEntry> LoadTableOfContents(
            BookArchive archive,
            List<ManifestItem> manifest,
            Dictionary<string, ManifestItem> manifestById,
            string packagePath,
            string ncxId)
        {
            var entries = new List<TocEntry>();
            // EPUB3 优先使用 nav 文档，旧版 EPUB2 再回退到 NCX。
            ManifestItem navItem = null;
            foreach (ManifestItem item in manifest)
            {
                if (ContainsToken(item.Properties, "nav"))
                {
                    navItem = item;
                    break;
                }
            }

            if (navItem != null && archive.Exists(navItem.Path))
            {
                XmlDocument nav = LoadXml(archive.ReadRequired(navItem.Path, "EPUB 缺少导航文件。"), navItem.Path);
                XmlElement navElement = FindTocNav(nav.DocumentElement);
                XmlElement list = navElement == null ? null : FindFirstDescendant(navElement, "ol");
                if (list != null)
                {
                    ParseNavigationList(list, navItem.Path, 0, entries);
                }
            }

            if (entries.Count > 0)
            {
                return entries;
            }

            ManifestItem ncxItem = null;
            if (!string.IsNullOrEmpty(ncxId))
            {
                manifestById.TryGetValue(ncxId, out ncxItem);
            }
            if (ncxItem == null)
            {
                foreach (ManifestItem item in manifest)
                {
                    if (string.Equals(item.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetExtension(item.Path), ".ncx", StringComparison.OrdinalIgnoreCase))
                    {
                        ncxItem = item;
                        break;
                    }
                }
            }

            if (ncxItem != null && archive.Exists(ncxItem.Path))
            {
                XmlDocument ncx = LoadXml(archive.ReadRequired(ncxItem.Path, "EPUB 缺少 NCX 目录。"), ncxItem.Path);
                XmlElement navMap = FindFirstDescendant(ncx.DocumentElement, "navMap");
                if (navMap != null)
                {
                    foreach (XmlElement navPoint in ChildElements(navMap, "navPoint"))
                    {
                        ParseNavPoint(navPoint, ncxItem.Path, 0, entries);
                    }
                }
            }
            return entries;
        }

        private static XmlElement FindTocNav(XmlElement root)
        {
            foreach (XmlElement element in DescendantElements(root))
            {
                if (!string.Equals(element.LocalName, "nav", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string type = GetAttribute(element, "type");
                if (ContainsToken(type, "toc"))
                {
                    return element;
                }
            }

            foreach (XmlElement element in DescendantElements(root))
            {
                if (string.Equals(element.LocalName, "nav", StringComparison.OrdinalIgnoreCase)
                    && FindFirstDescendant(element, "ol") != null)
                {
                    return element;
                }
            }
            return null;
        }

        private static void ParseNavigationList(
            XmlElement list,
            string basePath,
            int depth,
            List<TocEntry> entries)
        {
            foreach (XmlElement listItem in ChildElements(list, "li"))
            {
                XmlElement anchor = null;
                XmlElement nestedList = null;
                foreach (XmlElement child in ChildElements(listItem))
                {
                    if (anchor == null && string.Equals(child.LocalName, "a", StringComparison.OrdinalIgnoreCase))
                    {
                        anchor = child;
                    }
                    else if (nestedList == null
                        && (string.Equals(child.LocalName, "ol", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(child.LocalName, "ul", StringComparison.OrdinalIgnoreCase)))
                    {
                        nestedList = child;
                    }
                }

                if (anchor != null)
                {
                    TocEntry entry = CreateTocEntry(
                        CleanText(anchor.InnerText),
                        GetAttribute(anchor, "href"),
                        basePath,
                        depth);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }

                if (nestedList != null)
                {
                    ParseNavigationList(nestedList, basePath, depth + 1, entries);
                }
            }
        }

        private static void ParseNavPoint(
            XmlElement navPoint,
            string basePath,
            int depth,
            List<TocEntry> entries)
        {
            string label = null;
            XmlElement labelElement = FindFirstDescendant(navPoint, "navLabel");
            if (labelElement != null)
            {
                XmlElement textElement = FindFirstDescendant(labelElement, "text");
                if (textElement != null)
                {
                    label = CleanText(textElement.InnerText);
                }
            }

            XmlElement content = FindFirstDescendant(navPoint, "content");
            if (content != null)
            {
                TocEntry entry = CreateTocEntry(label, GetAttribute(content, "src"), basePath, depth);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            foreach (XmlElement child in ChildElements(navPoint, "navPoint"))
            {
                ParseNavPoint(child, basePath, depth + 1, entries);
            }
        }

        private static TocEntry CreateTocEntry(string label, string href, string basePath, int depth)
        {
            if (string.IsNullOrWhiteSpace(href)
                || href.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string filePart;
            string fragment;
            SplitHref(href, out filePart, out fragment);
            return new TocEntry
            {
                Label = string.IsNullOrWhiteSpace(label) ? "未命名章节" : label,
                FilePath = ResolvePath(basePath, filePart),
                Fragment = fragment,
                Depth = depth
            };
        }

        private static List<NovelChapter> BuildChapters(
            BookArchive archive,
            List<ManifestItem> spineItems,
            List<ManifestItem> manifest,
            List<TocEntry> toc)
        {
            var chapters = new List<NovelChapter>();
            foreach (ManifestItem spineItem in spineItems)
            {
                if (!archive.Exists(spineItem.Path))
                {
                    continue;
                }

                ContentDocument content;
                try
                {
                    XmlDocument document = LoadXml(
                        archive.ReadRequired(spineItem.Path, "EPUB 缺少正文文件。"),
                        spineItem.Path);
                    content = ExtractContent(document, archive, manifest, spineItem.Path);
                }
                catch (XmlException)
                {
                    // 容错处理少量不严格符合 XML 的 EPUB 正文。
                    string text = ExtractFallbackText(archive.ReadRequired(spineItem.Path, "EPUB 缺少正文文件。"));
                    var blocks = new List<NovelBlock>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        blocks.Add(new NovelBlock(
                            NovelBlockKind.Paragraph,
                            new List<NovelInline>
                            {
                                new NovelInline(CleanText(text), false, false, false)
                            }));
                    }
                    content = new ContentDocument(blocks);
                }

                var matchingToc = new List<TocEntry>();
                foreach (TocEntry entry in toc)
                {
                    if (string.Equals(entry.FilePath, spineItem.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingToc.Add(entry);
                    }
                }

                if (matchingToc.Count == 0)
                {
                    AddChapter(
                        chapters,
                        GetContentTitle(content, spineItem.Path),
                        content.Blocks,
                        0);
                    continue;
                }

                var rangedToc = new List<TocRange>();
                foreach (TocEntry entry in matchingToc)
                {
                    int start = FindTocStart(content, entry);
                    rangedToc.Add(new TocRange
                    {
                        Entry = entry,
                        Start = start < 0 ? 0 : start
                    });
                }
                rangedToc.Sort(delegate(TocRange left, TocRange right)
                {
                    // 常见多级目录会让父级和子级指向同一文件，保留更具体的子级链接。
                    int result = left.Start.CompareTo(right.Start);
                    return result != 0 ? result : right.Entry.Depth.CompareTo(left.Entry.Depth);
                });
                var uniqueRanges = new List<TocRange>();
                foreach (TocRange range in rangedToc)
                {
                    if (uniqueRanges.Count == 0 || uniqueRanges[uniqueRanges.Count - 1].Start != range.Start)
                    {
                        uniqueRanges.Add(range);
                    }
                }
                rangedToc = uniqueRanges;

                if (rangedToc[0].Start > 0)
                {
                    AddChapter(
                        chapters,
                        "开篇",
                        SliceBlocks(content.Blocks, 0, rangedToc[0].Start),
                        0);
                }

                for (int i = 0; i < rangedToc.Count; i++)
                {
                    TocRange current = rangedToc[i];
                    int end = content.Blocks.Count;
                    for (int j = i + 1; j < rangedToc.Count; j++)
                    {
                        if (rangedToc[j].Start > current.Start)
                        {
                            end = rangedToc[j].Start;
                            break;
                        }
                    }

                    string title = string.IsNullOrWhiteSpace(current.Entry.Label)
                        ? GetContentTitle(content, spineItem.Path)
                        : current.Entry.Label;
                    AddChapter(
                        chapters,
                        title,
                        SliceBlocks(content.Blocks, current.Start, end),
                        current.Entry.Depth);
                }
            }
            return chapters;
        }

        private static void AddChapter(
            List<NovelChapter> chapters,
            string title,
            List<NovelBlock> blocks,
            int depth)
        {
            if (blocks == null)
            {
                blocks = new List<NovelBlock>();
            }

            var text = new StringBuilder();
            foreach (NovelBlock block in blocks)
            {
                string plain = block.GetPlainText();
                if (!string.IsNullOrEmpty(plain))
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }
                    text.Append(plain);
                }
            }

            chapters.Add(new NovelChapter(
                string.IsNullOrWhiteSpace(title) ? "未命名章节" : title,
                text.ToString(),
                blocks,
                Math.Max(0, depth)));
        }

        private static List<NovelBlock> SliceBlocks(List<NovelBlock> blocks, int start, int end)
        {
            var result = new List<NovelBlock>();
            int safeStart = Math.Max(0, Math.Min(start, blocks.Count));
            int safeEnd = Math.Max(safeStart, Math.Min(end, blocks.Count));
            for (int i = safeStart; i < safeEnd; i++)
            {
                result.Add(blocks[i]);
            }
            return result;
        }

        private static int FindTocStart(ContentDocument content, TocEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Fragment))
            {
                return 0;
            }

            XmlNode anchor = content.FindAnchor(entry.Fragment);
            if (anchor == null)
            {
                return 0;
            }

            int order;
            if (!content.Orders.TryGetValue(anchor, out order))
            {
                return 0;
            }

            for (int i = 0; i < content.SourceBlocks.Count; i++)
            {
                if (content.SourceBlocks[i].EndOrder >= order)
                {
                    return i;
                }
            }
            return 0;
        }

        private static string GetContentTitle(ContentDocument content, string filePath)
        {
            foreach (SourceBlock sourceBlock in content.SourceBlocks)
            {
                if (sourceBlock.Block.Kind == NovelBlockKind.Heading)
                {
                    string title = CleanText(sourceBlock.Block.GetPlainText());
                    if (!string.IsNullOrEmpty(title))
                    {
                        return title;
                    }
                }
            }

            string name = Path.GetFileNameWithoutExtension(filePath);
            return string.IsNullOrEmpty(name) ? "未命名章节" : name;
        }

        private static ContentDocument ExtractContent(
            XmlDocument document,
            BookArchive archive,
            List<ManifestItem> manifest,
            string documentPath)
        {
            XmlElement body = FindFirstDescendant(document.DocumentElement, "body");
            if (body == null)
            {
                return new ContentDocument(new List<NovelBlock>());
            }

            var orders = new Dictionary<XmlNode, int>();
            var lastOrders = new Dictionary<XmlNode, int>();
            int nextOrder = 0;
            // 记录节点的文档顺序，用于把目录锚点映射到切分后的富文本块。
            AssignNodeOrders(body, orders, lastOrders, ref nextOrder);

            var content = new ContentDocument(new List<NovelBlock>());
            content.Orders = orders;
            var context = new ExtractContext
            {
                Archive = archive,
                Manifest = manifest,
                DocumentPath = documentPath,
                Orders = orders,
                LastOrders = lastOrders
            };
            ExtractContainer(body, context, content.SourceBlocks);
            foreach (SourceBlock sourceBlock in content.SourceBlocks)
            {
                content.Blocks.Add(sourceBlock.Block);
            }
            return content;
        }

        private static void AssignNodeOrders(
            XmlNode node,
            Dictionary<XmlNode, int> orders,
            Dictionary<XmlNode, int> lastOrders,
            ref int nextOrder)
        {
            orders[node] = nextOrder++;
            foreach (XmlNode child in node.ChildNodes)
            {
                AssignNodeOrders(child, orders, lastOrders, ref nextOrder);
            }
            lastOrders[node] = nextOrder - 1;
        }

        private static void ExtractContainer(
            XmlElement container,
            ExtractContext context,
            List<SourceBlock> output)
        {
            // 连续的行内节点合并为段落，遇到块级节点时先冲刷当前段落。
            var pendingInlineNodes = new List<XmlNode>();
            foreach (XmlNode child in container.ChildNodes)
            {
                var element = child as XmlElement;
                if (element == null)
                {
                    if (child.NodeType == XmlNodeType.Text
                        || child.NodeType == XmlNodeType.CDATA
                        || child.NodeType == XmlNodeType.EntityReference)
                    {
                        pendingInlineNodes.Add(child);
                    }
                    continue;
                }

                string name = element.LocalName.ToLowerInvariant();
                if (IsIgnoredElement(name))
                {
                    continue;
                }

                if (IsBlockElement(name) || IsImageElement(name))
                {
                    FlushInlineNodes(pendingInlineNodes, context, output);
                    ExtractBlockElement(element, context, output);
                }
                else
                {
                    pendingInlineNodes.Add(element);
                }
            }
            FlushInlineNodes(pendingInlineNodes, context, output);
        }

        private static void ExtractBlockElement(
            XmlElement element,
            ExtractContext context,
            List<SourceBlock> output)
        {
            string name = element.LocalName.ToLowerInvariant();
            if (IsImageElement(name))
            {
                NovelImage image = ReadImageElement(element, context);
                if (image != null)
                {
                    AddSourceBlock(
                        output,
                        new NovelBlock(
                            NovelBlockKind.Paragraph,
                            new List<NovelInline> { new NovelInline(image) }),
                        element,
                        context);
                }
                return;
            }

            if (string.Equals(name, "hr", StringComparison.OrdinalIgnoreCase))
            {
                AddSourceBlock(
                    output,
                    new NovelBlock(NovelBlockKind.Rule, new List<NovelInline>()),
                    element,
                    context);
                return;
            }

            if (string.Equals(name, "svg", StringComparison.OrdinalIgnoreCase))
            {
                XmlElement imageElement = FindFirstDescendant(element, "image");
                if (imageElement != null)
                {
                    NovelImage image = ReadImageElement(imageElement, context);
                    if (image != null)
                    {
                        AddSourceBlock(
                            output,
                            new NovelBlock(
                                NovelBlockKind.Paragraph,
                                new List<NovelInline> { new NovelInline(image) }),
                            element,
                            context);
                    }
                }
                return;
            }

            if (HasNestedBlockElements(element)
                && !string.Equals(name, "pre", StringComparison.OrdinalIgnoreCase))
            {
                context.QuoteDepth += string.Equals(name, "blockquote", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                context.ListDepth += (string.Equals(name, "ul", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "ol", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                ExtractContainer(element, context, output);
                context.ListDepth -= (string.Equals(name, "ul", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "ol", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
                context.QuoteDepth -= string.Equals(name, "blockquote", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                return;
            }

            var inlines = new List<NovelInline>();
            bool preserveWhitespace = string.Equals(name, "pre", StringComparison.OrdinalIgnoreCase);
            CollectInlines(element, false, false, false, preserveWhitespace, context, inlines);
            RemoveEmptyInlines(inlines);
            if (inlines.Count == 0)
            {
                return;
            }

            NovelBlockKind kind = NovelBlockKind.Paragraph;
            int headingLevel = 0;
            if (name.Length == 2
                && name[0] == 'h'
                && name[1] >= '1'
                && name[1] <= '6')
            {
                kind = NovelBlockKind.Heading;
                headingLevel = name[1] - '0';
            }
            else if (string.Equals(name, "li", StringComparison.OrdinalIgnoreCase))
            {
                kind = NovelBlockKind.ListItem;
                inlines.Insert(0, new NovelInline("• ", false, false, false));
            }
            else if (string.Equals(name, "blockquote", StringComparison.OrdinalIgnoreCase)
                || context.QuoteDepth > 0)
            {
                kind = NovelBlockKind.Quote;
            }

            var block = new NovelBlock(kind, inlines)
            {
                Level = kind == NovelBlockKind.Heading ? headingLevel : context.ListDepth
            };
            ApplyAlignment(element, block);
            AddSourceBlock(output, block, element, context);
        }

        private static void FlushInlineNodes(
            List<XmlNode> nodes,
            ExtractContext context,
            List<SourceBlock> output)
        {
            if (nodes.Count == 0)
            {
                return;
            }

            var inlines = new List<NovelInline>();
            foreach (XmlNode node in nodes)
            {
                var element = node as XmlElement;
                if (element != null)
                {
                    string name = element.LocalName.ToLowerInvariant();
                    if (IsImageElement(name))
                    {
                        NovelImage image = ReadImageElement(element, context);
                        if (image != null)
                        {
                            inlines.Add(new NovelInline(image));
                        }
                        continue;
                    }

                    CollectInlines(element, false, false, false, false, context, inlines);
                }
                else
                {
                    AppendText(inlines, node.Value, false, false, false, false);
                }
            }
            RemoveEmptyInlines(inlines);
            if (inlines.Count > 0)
            {
                output.Add(new SourceBlock
                {
                    Block = new NovelBlock(
                        context.QuoteDepth > 0 ? NovelBlockKind.Quote : NovelBlockKind.Paragraph,
                        inlines),
                    EndOrder = context.LastOrders[nodes[nodes.Count - 1]]
                });
            }
            nodes.Clear();
        }

        private static void CollectInlines(
            XmlNode node,
            bool bold,
            bool italic,
            bool underline,
            bool preserveWhitespace,
            ExtractContext context,
            List<NovelInline> output)
        {
            if (node.NodeType == XmlNodeType.Text || node.NodeType == XmlNodeType.CDATA)
            {
                AppendText(output, node.Value, bold, italic, underline, preserveWhitespace);
                return;
            }

            var element = node as XmlElement;
            if (element == null)
            {
                return;
            }

            string name = element.LocalName.ToLowerInvariant();
            if (IsIgnoredElement(name))
            {
                return;
            }

            if (string.Equals(name, "br", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(new NovelInline(NovelInlineKind.LineBreak));
                return;
            }

            if (IsImageElement(name))
            {
                NovelImage image = ReadImageElement(element, context);
                if (image != null)
                {
                    output.Add(new NovelInline(image));
                }
                return;
            }

            if (string.Equals(name, "svg", StringComparison.OrdinalIgnoreCase))
            {
                XmlElement imageElement = FindFirstDescendant(element, "image");
                if (imageElement != null)
                {
                    NovelImage image = ReadImageElement(imageElement, context);
                    if (image != null)
                    {
                        output.Add(new NovelInline(image));
                    }
                }
                return;
            }

            string style = GetAttribute(element, "style");
            bool nextBold = bold || IsBoldElement(name) || StyleContains(style, "font-weight", "bold")
                || StyleContains(style, "font-weight", "700")
                || StyleContains(style, "font-weight", "800")
                || StyleContains(style, "font-weight", "900");
            bool nextItalic = italic || IsItalicElement(name) || StyleContains(style, "font-style", "italic");
            bool nextUnderline = underline || IsUnderlineElement(name)
                || StyleContains(style, "text-decoration", "underline");
            bool nextPreserve = preserveWhitespace
                || string.Equals(name, "pre", StringComparison.OrdinalIgnoreCase)
                || StyleContains(style, "white-space", "pre");

            foreach (XmlNode child in element.ChildNodes)
            {
                CollectInlines(
                    child,
                    nextBold,
                    nextItalic,
                    nextUnderline,
                    nextPreserve,
                    context,
                    output);
            }
        }

        private static void AppendText(
            List<NovelInline> output,
            string rawText,
            bool bold,
            bool italic,
            bool underline,
            bool preserveWhitespace)
        {
            if (string.IsNullOrEmpty(rawText))
            {
                return;
            }

            string text = rawText.Replace("\r\n", "\n").Replace('\r', '\n');
            if (!preserveWhitespace)
            {
                text = Regex.Replace(text, @"[ \t\f\v\u00A0]+", " ");
                text = Regex.Replace(text, @" *\n *", "\n");
            }

            if (text.Length == 0)
            {
                return;
            }
            if (!preserveWhitespace && char.IsWhiteSpace(text[0]) && EndsWithWhitespace(output))
            {
                text = text.TrimStart();
            }
            if (text.Length > 0)
            {
                output.Add(new NovelInline(text, bold, italic, underline));
            }
        }

        private static bool EndsWithWhitespace(List<NovelInline> inlines)
        {
            if (inlines.Count == 0)
            {
                return true;
            }

            NovelInline last = inlines[inlines.Count - 1];
            return last.Kind != NovelInlineKind.Text
                || string.IsNullOrEmpty(last.Text)
                || char.IsWhiteSpace(last.Text[last.Text.Length - 1]);
        }

        private static void RemoveEmptyInlines(List<NovelInline> inlines)
        {
            for (int i = inlines.Count - 1; i >= 0; i--)
            {
                NovelInline inline = inlines[i];
                if (inline.Kind == NovelInlineKind.Text)
                {
                    inline.Text = inline.Text ?? string.Empty;
                    if (i == 0)
                    {
                        inline.Text = inline.Text.TrimStart();
                    }
                    if (i == inlines.Count - 1)
                    {
                        inline.Text = inline.Text.TrimEnd();
                    }
                    if (inline.Text.Length == 0)
                    {
                        inlines.RemoveAt(i);
                    }
                }
            }
        }

        private static NovelImage ReadImageElement(XmlElement element, ExtractContext context)
        {
            string source = GetAttribute(element, "src");
            if (string.IsNullOrEmpty(source))
            {
                source = GetAttribute(element, "href", XLinkNamespace);
            }
            if (string.IsNullOrEmpty(source))
            {
                source = GetAttribute(element, "href");
            }
            if (string.IsNullOrEmpty(source))
            {
                source = GetAttribute(element, "data");
            }
            if (string.IsNullOrEmpty(source))
            {
                return null;
            }

            string path = ResolvePath(context.DocumentPath, source);
            byte[] data;
            if (!context.Archive.TryRead(path, out data))
            {
                return null;
            }

            string mediaType = FindMediaType(context.Manifest, path);
            if (string.IsNullOrEmpty(mediaType))
            {
                mediaType = GuessImageMediaType(path);
            }
            if (string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string alt = GetAttribute(element, "alt");
            return new NovelImage(data, mediaType, alt);
        }

        private static bool HasNestedBlockElements(XmlElement element)
        {
            foreach (XmlElement child in ChildElements(element))
            {
                string name = child.LocalName.ToLowerInvariant();
                if (IsBlockElement(name) || IsImageElement(name))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsBlockElement(string name)
        {
            switch (name)
            {
                case "address":
                case "article":
                case "aside":
                case "blockquote":
                case "body":
                case "caption":
                case "center":
                case "dd":
                case "div":
                case "dl":
                case "dt":
                case "figcaption":
                case "figure":
                case "footer":
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                case "header":
                case "hr":
                case "li":
                case "main":
                case "nav":
                case "ol":
                case "p":
                case "pre":
                case "section":
                case "table":
                case "tbody":
                case "td":
                case "tfoot":
                case "th":
                case "thead":
                case "tr":
                case "ul":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsImageElement(string name)
        {
            return string.Equals(name, "img", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "image", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredElement(string name)
        {
            return string.Equals(name, "script", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "style", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "noscript", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "head", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "nav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "video", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBoldElement(string name)
        {
            return string.Equals(name, "b", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "strong", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsItalicElement(string name)
        {
            return string.Equals(name, "i", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "em", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnderlineElement(string name)
        {
            return string.Equals(name, "u", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ins", StringComparison.OrdinalIgnoreCase);
        }

        private static bool StyleContains(string style, string property, string value)
        {
            if (string.IsNullOrEmpty(style))
            {
                return false;
            }

            foreach (string declaration in style.Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                string name = declaration.Substring(0, colon).Trim();
                string content = declaration.Substring(colon + 1).Trim();
                if (string.Equals(name, property, StringComparison.OrdinalIgnoreCase)
                    && content.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void ApplyAlignment(XmlElement element, NovelBlock block)
        {
            string alignment = GetAttribute(element, "align");
            if (string.IsNullOrEmpty(alignment))
            {
                alignment = GetStyleValue(GetAttribute(element, "style"), "text-align");
            }
            if (string.IsNullOrEmpty(alignment)
                && string.Equals(element.LocalName, "center", StringComparison.OrdinalIgnoreCase))
            {
                alignment = "center";
            }

            if (string.Equals(alignment, "center", StringComparison.OrdinalIgnoreCase))
            {
                block.Alignment = NovelTextAlignment.Center;
            }
            else if (string.Equals(alignment, "right", StringComparison.OrdinalIgnoreCase)
                || string.Equals(alignment, "end", StringComparison.OrdinalIgnoreCase))
            {
                block.Alignment = NovelTextAlignment.Right;
            }
            else if (string.Equals(alignment, "justify", StringComparison.OrdinalIgnoreCase))
            {
                block.Alignment = NovelTextAlignment.Justify;
            }
            else if (string.Equals(alignment, "left", StringComparison.OrdinalIgnoreCase)
                || string.Equals(alignment, "start", StringComparison.OrdinalIgnoreCase))
            {
                block.Alignment = NovelTextAlignment.Left;
            }
        }

        private static string GetStyleValue(string style, string property)
        {
            if (string.IsNullOrEmpty(style))
            {
                return null;
            }

            foreach (string declaration in style.Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                if (string.Equals(declaration.Substring(0, colon).Trim(), property, StringComparison.OrdinalIgnoreCase))
                {
                    return declaration.Substring(colon + 1).Trim();
                }
            }
            return null;
        }

        private static void AddSourceBlock(
            List<SourceBlock> output,
            NovelBlock block,
            XmlNode sourceNode,
            ExtractContext context)
        {
            output.Add(new SourceBlock
            {
                Block = block,
                EndOrder = context.LastOrders[sourceNode]
            });
        }

        private static string FindMediaType(List<ManifestItem> manifest, string path)
        {
            foreach (ManifestItem item in manifest)
            {
                if (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    return item.MediaType;
                }
            }
            return null;
        }

        private static string GuessImageMediaType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                case ".gif":
                    return "image/gif";
                case ".bmp":
                    return "image/bmp";
                case ".webp":
                    return "image/webp";
                case ".svg":
                    return "image/svg+xml";
                default:
                    return null;
            }
        }

        private static bool IsContentItem(ManifestItem item)
        {
            if (string.Equals(item.MediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string extension = Path.GetExtension(item.Path);
            return string.Equals(extension, ".xhtml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase);
        }

        private static XmlDocument LoadXml(byte[] data, string sourceName)
        {
            string markup = DecodeMarkup(data);
            // XML 解析器不认识 XHTML 常用实体，先转换成等价的 XML 转义文本。
            markup = ReplaceNamedEntities(markup);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                CheckCharacters = false,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            };

            var document = new XmlDocument { XmlResolver = null };
            try
            {
                using (var textReader = new StringReader(markup))
                using (XmlReader reader = XmlReader.Create(textReader, settings))
                {
                    document.Load(reader);
                }
                return document;
            }
            catch (XmlException ex)
            {
                throw new XmlException(
                    string.Format(CultureInfo.CurrentCulture, "{0} 不是有效的 XML：{1}", sourceName, ex.Message),
                    ex,
                    ex.LineNumber,
                    ex.LinePosition);
            }
        }

        private static string DecodeMarkup(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return string.Empty;
            }

            if (HasByteOrderMark(data))
            {
                return NovelBook.DecodeText(data).TrimStart('\uFEFF');
            }

            int probeLength = Math.Min(data.Length, 2048);
            string probe = Encoding.ASCII.GetString(data, 0, probeLength);
            Match match = EncodingDeclaration.Match(probe);
            if (match.Success)
            {
                try
                {
                    Encoding encoding = Encoding.GetEncoding(match.Groups["name"].Value);
                    return encoding.GetString(data).TrimStart('\uFEFF');
                }
                catch (ArgumentException)
                {
                    // 声明了系统不支持的编码时回退到自动识别。
                }
            }

            return NovelBook.DecodeText(data).TrimStart('\uFEFF');
        }

        private static bool HasByteOrderMark(byte[] data)
        {
            return (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                || (data.Length >= 2
                    && ((data[0] == 0xFF && data[1] == 0xFE)
                        || (data[0] == 0xFE && data[1] == 0xFF)));
        }

        private static string ReplaceNamedEntities(string markup)
        {
            return NamedEntity.Replace(markup, delegate(Match match)
            {
                string source = match.Value;
                string name = match.Groups["name"].Value;
                if (string.Equals(name, "amp", StringComparison.Ordinal)
                    || string.Equals(name, "lt", StringComparison.Ordinal)
                    || string.Equals(name, "gt", StringComparison.Ordinal)
                    || string.Equals(name, "quot", StringComparison.Ordinal)
                    || string.Equals(name, "apos", StringComparison.Ordinal))
                {
                    return source;
                }

                string decoded = WebUtility.HtmlDecode(source);
                return string.Equals(decoded, source, StringComparison.Ordinal)
                    ? source
                    : EscapeXmlText(decoded);
            });
        }

        private static string EscapeXmlText(string text)
        {
            return text.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static string ExtractFallbackText(byte[] data)
        {
            string markup = DecodeMarkup(data);
            markup = Regex.Replace(
                markup,
                @"<(script|style)\b[^>]*>.*?</\1\s*>",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            markup = Regex.Replace(markup, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            markup = Regex.Replace(markup, @"</(?:p|div|h[1-6]|li|blockquote)\s*>", "\n", RegexOptions.IgnoreCase);
            markup = Regex.Replace(markup, @"<[^>]+>", string.Empty);
            markup = WebUtility.HtmlDecode(markup);
            return markup.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        private static string ResolvePath(string baseFile, string href)
        {
            // 先去掉查询串与锚点，再相对当前文档目录解析并归一化 ZIP 内路径。
            if (string.IsNullOrWhiteSpace(href))
            {
                return NormalizeArchivePath(baseFile);
            }

            string clean = href.Trim();
            int fragment = clean.IndexOf('#');
            if (fragment >= 0)
            {
                clean = clean.Substring(0, fragment);
            }
            int query = clean.IndexOf('?');
            if (query >= 0)
            {
                clean = clean.Substring(0, query);
            }
            if (clean.Length == 0)
            {
                return NormalizeArchivePath(baseFile);
            }

            try
            {
                clean = Uri.UnescapeDataString(clean);
            }
            catch (UriFormatException)
            {
                // 保留原始路径，交给 ZIP 查找逻辑处理。
            }

            string directory = GetDirectory(baseFile);
            if (clean[0] == '/')
            {
                directory = string.Empty;
                clean = clean.TrimStart('/');
            }
            return NormalizeArchivePath(CombinePath(directory, clean));
        }

        private static void SplitHref(string href, out string filePart, out string fragment)
        {
            int hash = href.IndexOf('#');
            if (hash >= 0)
            {
                filePart = href.Substring(0, hash);
                fragment = href.Substring(hash + 1);
            }
            else
            {
                filePart = href;
                fragment = string.Empty;
            }

            int query = filePart.IndexOf('?');
            if (query >= 0)
            {
                filePart = filePart.Substring(0, query);
            }
            try
            {
                fragment = Uri.UnescapeDataString(fragment);
            }
            catch (UriFormatException)
            {
                // 无效转义时使用原值。
            }
        }

        private static string NormalizeArchivePath(string path)
        {
            string clean = path == null ? string.Empty : path.Replace('\\', '/');
            var parts = new List<string>();
            foreach (string part in clean.Split('/'))
            {
                if (part.Length == 0 || string.Equals(part, ".", StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(part, "..", StringComparison.Ordinal))
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                    continue;
                }
                parts.Add(part);
            }
            return string.Join("/", parts.ToArray());
        }

        private static string CombinePath(string directory, string path)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return path;
            }
            return directory + "/" + path;
        }

        private static string GetDirectory(string path)
        {
            string normalized = NormalizeArchivePath(path);
            int slash = normalized.LastIndexOf('/');
            return slash < 0 ? string.Empty : normalized.Substring(0, slash);
        }

        private static XmlElement FindFirstDescendant(XmlNode node, string localName)
        {
            if (node == null)
            {
                return null;
            }

            foreach (XmlElement element in DescendantElements(node))
            {
                if (string.Equals(element.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }
            return null;
        }

        private static IEnumerable<XmlElement> DescendantElements(XmlNode node)
        {
            if (node == null)
            {
                yield break;
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                var element = child as XmlElement;
                if (element == null)
                {
                    continue;
                }
                yield return element;
                foreach (XmlElement nested in DescendantElements(element))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<XmlElement> ChildElements(XmlNode node)
        {
            if (node == null)
            {
                yield break;
            }
            foreach (XmlNode child in node.ChildNodes)
            {
                var element = child as XmlElement;
                if (element != null)
                {
                    yield return element;
                }
            }
        }

        private static IEnumerable<XmlElement> ChildElements(XmlNode node, string localName)
        {
            foreach (XmlElement element in ChildElements(node))
            {
                if (string.Equals(element.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return element;
                }
            }
        }

        private static string GetAttribute(XmlElement element, string localName)
        {
            return GetAttribute(element, localName, null);
        }

        private static string GetAttribute(XmlElement element, string localName, string namespaceUri)
        {
            if (element == null)
            {
                return null;
            }
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (string.Equals(attribute.LocalName, localName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(namespaceUri)
                        || string.Equals(attribute.NamespaceURI, namespaceUri, StringComparison.Ordinal)))
                {
                    return attribute.Value;
                }
            }
            return null;
        }

        private static bool ContainsToken(string value, string token)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (string part in value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(part, token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private sealed class BookArchive : IDisposable
        {
            private readonly ZipArchive _archive;
            private readonly Dictionary<string, ZipArchiveEntry> _entries =
                new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

            public BookArchive(Stream stream)
            {
                _archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
                foreach (ZipArchiveEntry entry in _archive.Entries)
                {
                    string path = NormalizeArchivePath(entry.FullName);
                    if (path.Length > 0 && !_entries.ContainsKey(path))
                    {
                        _entries.Add(path, entry);
                    }
                }
            }

            public bool Exists(string path)
            {
                return _entries.ContainsKey(NormalizeArchivePath(path));
            }

            public bool TryRead(string path, out byte[] data)
            {
                ZipArchiveEntry entry;
                if (!_entries.TryGetValue(NormalizeArchivePath(path), out entry))
                {
                    data = null;
                    return false;
                }

                using (Stream stream = entry.Open())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    data = memory.ToArray();
                    return true;
                }
            }

            public byte[] ReadRequired(string path, string errorMessage)
            {
                byte[] data;
                if (!TryRead(path, out data))
                {
                    throw new InvalidDataException(errorMessage);
                }
                return data;
            }

            public void Dispose()
            {
                _archive.Dispose();
            }
        }

        private sealed class ManifestItem
        {
            public string Id;
            public string Path;
            public string MediaType;
            public string Properties;
        }

        private sealed class TocEntry
        {
            public string Label;
            public string FilePath;
            public string Fragment;
            public int Depth;
        }

        private sealed class TocRange
        {
            public TocEntry Entry;
            public int Start;
        }

        private sealed class SourceBlock
        {
            public NovelBlock Block;
            public int EndOrder;
        }

        private sealed class ContentDocument
        {
            public ContentDocument(List<NovelBlock> blocks)
            {
                Blocks = blocks ?? new List<NovelBlock>();
                SourceBlocks = new List<SourceBlock>();
                Orders = new Dictionary<XmlNode, int>();
            }

            public List<NovelBlock> Blocks;
            public List<SourceBlock> SourceBlocks;
            public Dictionary<XmlNode, int> Orders;

            public XmlNode FindAnchor(string fragment)
            {
                foreach (KeyValuePair<XmlNode, int> pair in Orders)
                {
                    var element = pair.Key as XmlElement;
                    if (element == null)
                    {
                        continue;
                    }
                    string id = GetAttribute(element, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        id = GetAttribute(element, "name");
                    }
                    if (string.Equals(id, fragment, StringComparison.Ordinal))
                    {
                        return element;
                    }
                }
                return null;
            }
        }

        private sealed class ExtractContext
        {
            public BookArchive Archive;
            public List<ManifestItem> Manifest;
            public string DocumentPath;
            public Dictionary<XmlNode, int> Orders;
            public Dictionary<XmlNode, int> LastOrders;
            public int ListDepth;
            public int QuoteDepth;
        }
    }
}
