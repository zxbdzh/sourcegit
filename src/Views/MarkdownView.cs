using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SourceGit.Views
{
    public class MarkdownView : UserControl
    {
        public static readonly StyledProperty<string> MarkdownProperty =
            AvaloniaProperty.Register<MarkdownView, string>(nameof(Markdown));

        public string Markdown
        {
            get => GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        public MarkdownView()
        {
            Focusable = false;
            Content = _root;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Rebuild();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // 未入树时资源刷不出来，代码块会变成灰底灰字。
            if ((change.Property == MarkdownProperty || change.Property.Name == nameof(ActualThemeVariant)) && VisualRoot != null)
                Rebuild();
        }

        private void Rebuild()
        {
            _root.Children.Clear();
            if (string.IsNullOrWhiteSpace(Markdown))
                return;

            var document = Markdig.Markdown.Parse(Markdown, Pipeline);
            AddBlocks(_root, document);
        }

        private void AddBlocks(Panel panel, ContainerBlock container)
        {
            foreach (var block in container)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        panel.Children.Add(MakeParagraph(heading.Inline, heading.Level switch
                        {
                            1 => 16,
                            2 => 14,
                            _ => 13
                        }, FontWeight.Bold, 0, heading.Level <= 2 ? 4 : 0));
                        break;
                    case ParagraphBlock paragraph:
                        panel.Children.Add(MakeParagraph(paragraph.Inline, 13, FontWeight.Normal, 0, 0));
                        break;
                    case FencedCodeBlock code:
                        panel.Children.Add(MakeCode(code.Lines.ToString(), code.Info));
                        break;
                    case CodeBlock indented:
                        panel.Children.Add(MakeCode(indented.Lines.ToString(), null));
                        break;
                    case ListBlock list:
                        panel.Children.Add(MakeList(list));
                        break;
                    case QuoteBlock quote:
                        panel.Children.Add(MakeQuote(quote));
                        break;
                    case ThematicBreakBlock:
                        panel.Children.Add(new Border
                        {
                            Height = 1,
                            Margin = new Thickness(0, 6),
                            Background = Brush("Brush.Border2")
                        });
                        break;
                    case Table table:
                        panel.Children.Add(MakeTable(table));
                        break;
                    case LeafBlock leaf when leaf.Inline != null:
                        panel.Children.Add(MakeParagraph(leaf.Inline, 13, FontWeight.Normal, 0, 0));
                        break;
                    case ContainerBlock nested:
                        AddBlocks(panel, nested);
                        break;
                }
            }
        }

        private Control MakeParagraph(ContainerInline inline, double size, FontWeight weight, double indent, double top)
        {
            var text = new SelectableTextBlock
            {
                FontSize = size,
                FontWeight = weight,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(indent, top, 0, 0)
            };
            AddInlines(text.Inlines, inline, weight, FontStyle.Normal);
            return text;
        }

        private Control MakeCode(string code, string language)
        {
            var text = (code ?? string.Empty).TrimEnd();
            if (string.IsNullOrWhiteSpace(text))
                return new Panel();

            var panel = new StackPanel { Spacing = 2 };
            if (!string.IsNullOrWhiteSpace(language))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = language.Trim().Split(' ', '\t')[0],
                    FontSize = 11,
                    Opacity = 0.7
                });
            }

            var body = new SelectableTextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            body.Classes.Add("md_code_text");
            if (string.IsNullOrWhiteSpace(language))
                body.Text = text;
            else
                Models.TextMateHelper.Highlight(body.Inlines, text, language);
            panel.Children.Add(body);

            var border = new Border { Child = panel };
            border.Classes.Add("md_code");
            return border;
        }

        private Control MakeList(ListBlock list)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var index = 1;
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0)
                index = start;
            var row = 0;
            foreach (var item in list)
            {
                if (item is not ListItemBlock listItem)
                    continue;

                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var marker = new TextBlock
                {
                    Text = list.IsOrdered ? $"{index++}." : "·",
                    FontSize = 13,
                    Margin = new Thickness(4, 0, 8, 0),
                    Foreground = Brush("Brush.FG2")
                };
                Grid.SetRow(marker, row);
                Grid.SetColumn(marker, 0);
                grid.Children.Add(marker);

                var body = new StackPanel { Spacing = 6 };
                AddBlocks(body, listItem);
                Grid.SetRow(body, row);
                Grid.SetColumn(body, 1);
                grid.Children.Add(body);
                row++;
            }

            return grid;
        }

        private Control MakeQuote(QuoteBlock quote)
        {
            var body = new StackPanel { Spacing = 6 };
            AddBlocks(body, quote);
            return new Border
            {
                BorderBrush = Brush("Brush.Border0"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(8, 0, 0, 0),
                Child = body
            };
        }

        private Control MakeTable(Table table)
        {
            var grid = new Grid();
            var columnCount = table.ColumnDefinitions?.Count ?? 0;
            if (columnCount == 0)
            {
                foreach (var child in table)
                {
                    if (child is TableRow probe)
                        columnCount = Math.Max(columnCount, CountCells(probe));
                }
            }

            for (var i = 0; i < columnCount; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var row = 0;
            foreach (var child in table)
            {
                if (child is not TableRow tableRow)
                    continue;

                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var column = 0;
                foreach (var cellBlock in tableRow)
                {
                    if (cellBlock is not TableCell cell)
                        continue;

                    var cellBody = new StackPanel { Spacing = 2 };
                    var align = column < (table.ColumnDefinitions?.Count ?? 0)
                        ? table.ColumnDefinitions[column].Alignment
                        : null;
                    AddBlocks(cellBody, cell);
                    var alignment = align switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left
                    };
                    foreach (var control in cellBody.Children)
                    {
                        if (control is TextBlock text)
                            text.TextAlignment = alignment;
                    }

                    var border = new Border { Child = cellBody };
                    border.Classes.Add(tableRow.IsHeader ? "md_th" : "md_td");
                    Grid.SetRow(border, row);
                    Grid.SetColumn(border, column);
                    var span = Math.Max(1, cell.ColumnSpan);
                    if (span > 1)
                        Grid.SetColumnSpan(border, span);
                    grid.Children.Add(border);
                    column += span;
                }

                row++;
            }

            var frame = new Border { Child = grid, HorizontalAlignment = HorizontalAlignment.Stretch };
            frame.Classes.Add("md_table");
            return new ScrollViewer
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = frame
            };
        }

        private static int CountCells(TableRow row)
        {
            var count = 0;
            foreach (var cell in row)
            {
                if (cell is TableCell tableCell)
                    count += Math.Max(1, tableCell.ColumnSpan);
            }

            return count;
        }

        private void AddInlines(InlineCollection target, ContainerInline container, FontWeight weight, FontStyle style)
        {
            if (target == null || container == null)
                return;

            for (var inline = container.FirstChild; inline != null; inline = inline.NextSibling)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        target.Add(new Run(literal.Content.ToString())
                        {
                            FontWeight = weight,
                            FontStyle = style
                        });
                        break;
                    case CodeInline code:
                        var run = new Run(code.Content)
                        {
                            FontWeight = weight,
                            FontStyle = style
                        };
                        run.Classes.Add("inline_code");
                        target.Add(run);
                        break;
                    case EmphasisInline emphasis:
                        var nextWeight = emphasis.DelimiterCount >= 2 ? FontWeight.Bold : weight;
                        var nextStyle = emphasis.DelimiterCount % 2 == 1 ? FontStyle.Italic : style;
                        AddInlines(target, emphasis, nextWeight, nextStyle);
                        break;
                    case LineBreakInline:
                        target.Add(new LineBreak());
                        break;
                    case LinkInline { IsImage: true } image:
                        var alt = string.IsNullOrEmpty(image.FirstChild?.ToString()) ? image.Url : image.FirstChild.ToString();
                        target.Add(new Run(alt)
                        {
                            FontStyle = FontStyle.Italic,
                            Foreground = Brush("Brush.FG2")
                        });
                        break;
                    case LinkInline link:
                        var start = target.Count;
                        AddInlines(target, link, weight, style);
                        for (var i = start; i < target.Count; i++)
                        {
                            if (target[i] is Run linked)
                            {
                                linked.TextDecorations = TextDecorations.Underline;
                                linked.Foreground = Brush("Brush.Link");
                            }
                        }

                        break;
                    case AutolinkInline autolink:
                        target.Add(new Run(autolink.Url)
                        {
                            TextDecorations = TextDecorations.Underline,
                            Foreground = Brush("Brush.Link")
                        });
                        break;
                    case HtmlEntityInline entity:
                        target.Add(new Run(entity.Transcoded.ToString())
                        {
                            FontWeight = weight,
                            FontStyle = style
                        });
                        break;
                    case ContainerInline nested:
                        AddInlines(target, nested, weight, style);
                        break;
                }
            }
        }

        private IBrush Brush(string key)
        {
            if (this.TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
                return brush;
            return Brushes.Gray;
        }


        // 模型输出不可信，关掉 HTML 以免把标签当界面执行。
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .DisableHtml()
            .Build();

        private readonly StackPanel _root = new()
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }
}
