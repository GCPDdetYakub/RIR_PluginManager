using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RIR_PluginManager
{
    public enum NextAction { None, Rhino, Grasshopper }

    public sealed class ManagerWindow : Window
    {
        sealed class Row
        {
            public PluginGroup Group;
            public CheckBox Box;
            public Border Container;        // строка списка целиком (для выделения)
            public TextBlock Status;
            public IssueLevel? Level;      // null = замечаний нет
            public List<Issue> Issues;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly CheckBox _autoApply;
        readonly CheckBox _iconFix;
        readonly CheckBox _preload;
        readonly HashSet<string> _preloadExclude;
        Row _selected;
        static readonly Brush SelectedBrush = Theme.SelectedFill;
        static readonly Brush SelectedBorder = Theme.SelectedBorder;
        readonly TextBlock _checkStatus;
        readonly TextBox _details;
        readonly StackPanel _list;
        readonly Button _checkButton;
        bool _sortByResult;
        Button _sortAlpha, _sortResult;
        readonly List<PluginGroup> _groups;
        bool _checked;
        readonly bool _ghLoaded;

        public NextAction Next { get; private set; } = NextAction.None;

        public ManagerWindow()
        {
            Title = $"RIR_PluginManager — плагины Grasshopper для Rhino.Inside (Revit {PluginStore.RevitVersion})";
            Width = 1000; Height = 720; MinWidth = 700; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            // Стеклянная тема: своё оформление окна без системной рамки
            Resources = Theme.Load();
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Foreground = Theme.Fg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            var profile = PluginStore.LoadProfile();
            _ghLoaded = PluginStore.GrasshopperPluginsLoaded();

            var root = new DockPanel { Margin = new Thickness(10) };

            var header = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Text = "Отмеченные плагины загрузятся в Grasshopper. Снятые с отметки временно отключаются " +
                       "(файлы переименовываются в *.off) и возвращаются сразу после загрузки Grasshopper " +
                       "или при закрытии Revit. Щёлкните по названию плагина: замечания появятся в панели справа."
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var legend = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            legend.Inlines.Add(Glyph(IssueLevel.Error)); legend.Inlines.Add(" не загрузится   ");
            legend.Inlines.Add(Glyph(IssueLevel.Warning)); legend.Inlines.Add(" возможны проблемы   ");
            legend.Inlines.Add(Glyph(IssueLevel.Info)); legend.Inlines.Add(" к сведению   ");
            legend.Inlines.Add(Glyph(null)); legend.Inlines.Add(" замечаний нет");
            legend.Margin = new Thickness(0, 6, 0, 0);

            _checkStatus = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.FgDim,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "Нажмите «Проверить плагины», чтобы найти известные причины проблем на .NET 10. " +
                       "Файлы читаются без загрузки, на работу Revit это не влияет."
            };
            DockPanel.SetDock(_checkStatus, Dock.Top);
            root.Children.Add(_checkStatus);

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _checkButton = MakeButton("Проверить плагины", async (s, e) => await RunCheck());
            _checkButton.Style = (Style)Resources["PrimaryButton"];
            toolbar.Children.Add(_checkButton);
            toolbar.Children.Add(new TextBlock { Text = "Сортировка:", Foreground = Theme.FgDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 6) });
            _sortAlpha = MakeButton("А–Я", (s, e) => SetSort(false));
            _sortResult = MakeButton("По результату", (s, e) => SetSort(true));
            toolbar.Children.Add(_sortAlpha);
            toolbar.Children.Add(_sortResult);
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            if (_ghLoaded)
            {
                var warn = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Theme.Danger,
                    Margin = new Thickness(0, 0, 0, 8),
                    Text = "Профиль применён. Изменения применятся только после перезапуска Revit."
                };
                DockPanel.SetDock(warn, Dock.Top);
                root.Children.Add(warn);
            }

            // ----- нижняя часть -----
            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);

            _autoApply = new CheckBox
            {
                Content = "Применять профиль автоматически при запуске Revit",
                IsChecked = profile.AutoApply,
                Margin = new Thickness(0, 0, 0, 8)
            };
            bottom.Children.Add(_autoApply);

            _iconFix = new CheckBox
            {
                Content = $"Восстанавливать иконки старых плагинов без BinaryFormatter (сейчас: {IconFix.Status}; " +
                          "изменение действует после перезапуска Revit)",
                IsChecked = profile.IconFix,
                Margin = new Thickness(0, 0, 0, 8)
            };
            bottom.Children.Add(_iconFix);

            _preloadExclude = profile.PreloadExclude;
            _preload = new CheckBox
            {
                Content = "Заранее загружать новейшие версии общих библиотек плагинов (действует со следующего запуска Rhino)",
                IsChecked = profile.PreloadShared,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "Если несколько плагинов привозят одну библиотеку в разных версиях, до загрузки Grasshopper " +
                          "загружается самая новая — так все плагины смогут загрузиться. Исключения: строки preload_exclude= в профиле."
            };
            bottom.Children.Add(_preload);

            var selectRow = new WrapPanel();
            selectRow.Children.Add(MakeButton("Все", (s, e) => SetAll(true)));
            selectRow.Children.Add(MakeButton("Ни одного", (s, e) => SetAll(false)));
            selectRow.Children.Add(MakeButton("Снять ✖", (s, e) => UncheckRisky(IssueLevel.Error)));
            selectRow.Children.Add(MakeButton("Снять ✖ и ⚠", (s, e) => UncheckRisky(IssueLevel.Warning)));
            selectRow.Children.Add(MakeButton("Папка профиля", (s, e) => OpenDataDir()));
            selectRow.Children.Add(MakeButton("Вернуть все файлы", (s, e) => RestoreNow()));
            bottom.Children.Add(selectRow);

            var actionRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            actionRow.Children.Add(MakeButton("Применить", (s, e) => Finish(NextAction.None)));
            actionRow.Children.Add(MakeButton("Применить → Rhino", (s, e) => Finish(NextAction.Rhino)));
            actionRow.Children.Add(MakeButton("Применить → Grasshopper", (s, e) => Finish(NextAction.Grasshopper)));
            actionRow.Children.Add(MakeButton("Отмена", (s, e) => DialogResult = false));
            bottom.Children.Add(actionRow);

            root.Children.Add(bottom);

            // ----- список плагинов слева, подробности справа -----
            _list = new StackPanel();
            _groups = PluginStore.Scan();
            if (_groups.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Плагины не найдены. Дополнительные папки можно перечислить в файле folders.txt в папке профиля."
                });
                _checkButton.IsEnabled = false;
            }
            foreach (var g in _groups)
            {
                var status = new TextBlock
                {
                    Text = "",
                    Width = 22,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var label = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = $"{g.Name}    [{g.Source}, файлов: {g.Files.Count}" +
                           (g.DisabledFiles > 0 ? ", сейчас отключён]" : "]")
                };
                // Галочка отдельно от текста: клик по квадрату включает/выключает плагин,
                // клик по строке выделяет её и показывает замечания справа.
                var cb = new CheckBox
                {
                    IsChecked = !profile.Disabled.Contains(g.Key),
                    Margin = new Thickness(2, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(cb);
                content.Children.Add(status);
                content.Children.Add(label);

                var container = new Border
                {
                    Child = content,
                    Padding = new Thickness(2, 3, 2, 3),
                    Background = Brushes.Transparent,          // чтобы клик ловился по всей ширине строки
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var row = new Row { Group = g, Box = cb, Status = status, Container = container };
                container.PreviewMouseLeftButtonDown += (s, e) => Select(row);
                _rows.Add(row);
            }

            _details = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Padding = new Thickness(6),
                Text = "Выберите плагин в списке слева (щелчок по названию).\n\n" +
                       "Текст можно прокручивать и копировать (Ctrl+A, Ctrl+C)."
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });

            var listBorder = new Border
            {
                BorderBrush = Theme.GlassBorder,
                Background = Theme.PanelFill,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    Content = _list,
                    Padding = new Thickness(4),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            };
            // Легенда значков — прямо под списком
            var leftPanel = new DockPanel();
            DockPanel.SetDock(legend, Dock.Bottom);
            leftPanel.Children.Add(legend);
            var listCaption = SectionLabel("ПЛАГИНЫ");
            DockPanel.SetDock(listCaption, Dock.Top);
            leftPanel.Children.Add(listCaption);
            leftPanel.Children.Add(listBorder);
            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            var splitter = new GridSplitter
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            _details.Background = Brushes.Transparent;
            _details.Foreground = Theme.Fg;
            _details.BorderThickness = new Thickness(0);
            _details.CaretBrush = Theme.Fg;
            var detailsBorder = new Border
            {
                Child = _details,
                CornerRadius = new CornerRadius(12),
                Background = Theme.PanelFill,
                BorderBrush = Theme.GlassBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4)
            };
            var rightPanel = new DockPanel();
            var detailsCaption = SectionLabel("ЗАМЕЧАНИЯ");
            DockPanel.SetDock(detailsCaption, Dock.Top);
            rightPanel.Children.Add(detailsCaption);
            rightPanel.Children.Add(detailsBorder);
            Grid.SetColumn(rightPanel, 2);
            grid.Children.Add(rightPanel);

            root.Children.Add(grid);
            // Заголовок: перетаскивание окна и кнопка закрытия
            var titleBar = new DockPanel { Margin = new Thickness(18, 12, 12, 4), Background = Brushes.Transparent };
            var close = MakeButton("✕", (s, e) => DialogResult = false);
            close.Padding = new Thickness(10, 3, 10, 3);
            close.Margin = new Thickness(0);
            close.ToolTip = "Закрыть (Esc)";
            DockPanel.SetDock(close, Dock.Right);
            titleBar.Children.Add(close);
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Plugin Manager",
                FontSize = 24,
                FontWeight = FontWeights.Bold
            });
            titleStack.Children.Add(new Border
            {
                Width = 28, Height = 3, Background = Theme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4)
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = $"RIR_PluginManager  ·  Rhino.Inside  ·  Revit {PluginStore.RevitVersion}",
                FontSize = 11,
                Foreground = Theme.FgDim
            });
            titleBar.Children.Add(titleStack);
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            root.Margin = new Thickness(18, 6, 18, 16);
            var outer = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            outer.Children.Add(titleBar);
            outer.Children.Add(root);

            Content = new Border
            {
                Margin = new Thickness(14),                      // место для тени окна
                CornerRadius = new CornerRadius(18),
                Background = Theme.WindowBackground,
                BorderBrush = Theme.GlassBorder,
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.22, Color = Colors.Black
                },
                Child = outer
            };
            PreviewKeyDown += OnListKeyDown;
            UpdateSortButtons();

            Resort();
        }

        // ---------- управление с клавиатуры ----------
        // ↑/↓ и W/S — перемещение выделения, Enter — включить/выключить выбранный плагин.
        // W и S определяются по физическому положению клавиши (скан-коду), поэтому работают в любой раскладке.

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);
        const uint ScanW = 0x11, ScanS = 0x1F;

        static uint ScanCodeOf(System.Windows.Input.Key key)
        {
            try { return MapVirtualKey((uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), 0); }
            catch { return 0; }
        }

        void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; e.Handled = true; return; }

            // В поле подробностей клавиши работают как обычно
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem) return;
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;

            var key = e.Key == System.Windows.Input.Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            uint sc = ScanCodeOf(key);

            if (key == System.Windows.Input.Key.Up || sc == ScanW) { MoveSelection(-1); e.Handled = true; }
            else if (key == System.Windows.Input.Key.Down || sc == ScanS) { MoveSelection(+1); e.Handled = true; }
            else if ((key == System.Windows.Input.Key.Enter || key == System.Windows.Input.Key.Return) && _selected != null)
            {
                _selected.Box.IsChecked = _selected.Box.IsChecked != true;
                e.Handled = true;
            }
        }

        void MoveSelection(int delta)
        {
            var visible = _list.Children.OfType<Border>().ToList();
            if (visible.Count == 0) return;
            int index = _selected == null ? -1 : visible.IndexOf(_selected.Container);
            int next = index < 0 ? 0 : Math.Max(0, Math.Min(visible.Count - 1, index + delta));
            var row = _rows.FirstOrDefault(r => r.Container == visible[next]);
            if (row == null) return;
            Select(row);
            row.Container.BringIntoView();
        }

        // ---------- проверка ----------

        async System.Threading.Tasks.Task RunCheck()
        {
            _checkButton.IsEnabled = false;
            _checkStatus.Foreground = Theme.FgDim;
            _checkStatus.Text = "Проверка плагинов… (файлы читаются без загрузки, на работу Revit это не влияет)";
            foreach (var r in _rows) { r.Status.Text = "…"; r.Status.Foreground = Theme.FgDim; }

            var loadedSnapshot = PluginChecker.SnapshotLoaded();
            var groups = _groups;
            try
            {
                bool preloadOn = _preload.IsChecked == true;
                var disabledNow = new HashSet<string>(_rows.Where(r => r.Box.IsChecked != true).Select(r => r.Group.Key),
                                                      StringComparer.OrdinalIgnoreCase);
                var exclude = _preloadExclude;
                var results = await Task.Run(() =>
                {
                    Dictionary<string, SharedLibPreloader.Item> preload = null;
                    if (preloadOn)
                        preload = SharedLibPreloader.Plan(groups, disabledNow, exclude, loadedSnapshot)
                                                    .ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
                    return PluginChecker.Check(groups, loadedSnapshot, IconFix.Active, preload);
                });
                ShowResults(results);
                if (_selected != null) ShowDetails(_selected);
                LogResults(results);
                _checked = true;
                Resort();
            }
            catch (Exception ex)
            {
                _checkStatus.Foreground = Theme.Danger;
                _checkStatus.Text = "Проверку выполнить не удалось: " + ex.Message;
                PluginStore.Log("Check error: " + ex);
            }
            finally
            {
                _checkButton.IsEnabled = true;
            }
        }

        void LogResults(Dictionary<string, List<Issue>> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Результаты проверки плагинов:");
            foreach (var r in _rows.OrderBy(r => Rank(r.Level)).ThenBy(r => r.Group.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.AppendLine($"  {GlyphChar(r.Level)} {r.Group.Name} [{r.Group.Source}]");
                if (results.TryGetValue(r.Group.Key, out var issues))
                    foreach (var i in issues.OrderByDescending(i => i.Level))
                        sb.AppendLine($"      {GlyphChar(i.Level)} {i.Text}");
            }
            PluginStore.Log(sb.ToString().TrimEnd());
        }

        // ---------- сортировка ----------

        static int Rank(IssueLevel? level) => level switch
        {
            IssueLevel.Error => 0,
            IssueLevel.Warning => 1,
            IssueLevel.Info => 2,
            _ => 3
        };

        void Resort()
        {
            if (_list == null || _rows.Count == 0) return;
            IEnumerable<Row> ordered = _rows.OrderBy(r => r.Group.Name, StringComparer.CurrentCultureIgnoreCase);
            if (_sortByResult)
            {
                ordered = _checked
                    ? _rows.OrderBy(r => Rank(r.Level)).ThenBy(r => r.Group.Name, StringComparer.CurrentCultureIgnoreCase)
                    : ordered;
                if (!_checked)
                    _checkStatus.Text = "Сортировка по результату станет доступна после проверки: нажмите «Проверить плагины».";
            }
            _list.Children.Clear();
            foreach (var r in ordered) _list.Children.Add(r.Container);
        }

        void SetSort(bool byResult)
        {
            _sortByResult = byResult;
            UpdateSortButtons();
            Resort();
        }

        void UpdateSortButtons()
        {
            if (_sortAlpha == null || _sortResult == null) return;
            foreach (var b in new[] { _sortAlpha, _sortResult })
            {
                b.ClearValue(BackgroundProperty);
                b.ClearValue(ForegroundProperty);
            }
            var active = _sortByResult ? _sortResult : _sortAlpha;
            active.Background = Theme.ActiveFill;
            active.Foreground = Theme.FgInverse;
        }

        // ---------- панель подробностей ----------

        void Select(Row row)
        {
            if (_selected != null)
            {
                _selected.Container.Background = Brushes.Transparent;
                _selected.Container.BorderBrush = Brushes.Transparent;
                _selected.Container.ClearValue(System.Windows.Documents.TextElement.ForegroundProperty);
            }
            _selected = row;
            row.Container.Background = SelectedBrush;
            row.Container.BorderBrush = SelectedBorder;
            row.Container.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, Theme.FgInverse);
            ShowDetails(row);
        }

        void ShowDetails(Row row)
        {
            var g = row.Group;
            var sb = new StringBuilder();
            sb.AppendLine(g.Name);
            sb.AppendLine($"Источник: {g.Source}" + (g.DisabledFiles > 0 ? ", сейчас отключён" : ""));
            if (g.Folder != null) sb.AppendLine("Папка: " + g.Folder);
            sb.AppendLine();

            if (!_checked)
                sb.AppendLine("Проверка не выполнялась. Нажмите «Проверить плагины».");
            else if (row.Issues == null || row.Issues.Count == 0)
                sb.AppendLine("✔ Замечаний нет.");
            else
            {
                sb.AppendLine("Замечания:");
                foreach (var i in row.Issues.OrderByDescending(i => i.Level))
                {
                    sb.AppendLine($"{GlyphChar(i.Level)} {i.Text}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine("Файлы:");
            foreach (var f in g.Files) sb.AppendLine("  " + f);

            _details.Text = sb.ToString();
            _details.ScrollToHome();
        }

        // ---------- отображение результатов проверки ----------

        void ShowResults(Dictionary<string, List<Issue>> results)
        {
            int err = 0, warn = 0, info = 0, ok = 0;
            foreach (var row in _rows)
            {
                results.TryGetValue(row.Group.Key, out var issues);
                issues = issues ?? new List<Issue>();
                row.Level = issues.Count == 0 ? (IssueLevel?)null : issues.Max(i => i.Level);

                var glyph = Glyph(row.Level);
                row.Status.Text = glyph.Text;
                row.Status.Foreground = glyph.Foreground;

                row.Issues = issues;

                switch (row.Level)
                {
                    case IssueLevel.Error: err++; break;
                    case IssueLevel.Warning: warn++; break;
                    case IssueLevel.Info: info++; break;
                    default: ok++; break;
                }
            }
            _checkStatus.Foreground = Theme.Fg;
            _checkStatus.Text = $"Проверка завершена: ✖ {err}   ⚠ {warn}   ℹ {info}   ✔ {ok}. " +
                                "Это подсказка по известным причинам проблем на .NET 10, а не гарантия.";
        }

        static string GlyphChar(IssueLevel? level) => level switch
        {
            IssueLevel.Error => "✖",
            IssueLevel.Warning => "⚠",
            IssueLevel.Info => "ℹ",
            _ => "✔"
        };

        static System.Windows.Documents.Run Glyph(IssueLevel? level) => new System.Windows.Documents.Run(GlyphChar(level))
        {
            Foreground = level switch
            {
                IssueLevel.Error => Theme.Error,
                IssueLevel.Warning => Theme.Warning,
                IssueLevel.Info => Theme.Info,
                _ => Theme.Ok
            },
            FontWeight = FontWeights.Bold
        };

        // ---------- кнопки ----------

        static TextBlock SectionLabel(string text) => new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.FgDim,
            Margin = new Thickness(4, 0, 0, 6)
        };

        static Button MakeButton(string text, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                MinWidth = 80
            };
            b.Click += onClick;
            return b;
        }

        void SetAll(bool value)
        {
            foreach (var r in _rows) r.Box.IsChecked = value;
        }

        void UncheckRisky(IssueLevel threshold)
        {
            foreach (var r in _rows)
                if (r.Level.HasValue && r.Level.Value >= threshold)
                    r.Box.IsChecked = false;
            if (!_checked)
                MessageBox.Show(this, "Сначала нажмите «Проверить плагины».", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void OpenDataDir()
        {
            try
            {
                Directory.CreateDirectory(PluginStore.DataDir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + PluginStore.DataDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void RestoreNow()
        {
            // Возвращать имена безопасно в любой момент: уже загруженный Grasshopper их не перечитывает.
            var errors = PluginStore.RestoreAll();
            foreach (var e in errors) PluginStore.Log("RestoreNow: " + e);
            MessageBox.Show(this,
                errors.Count == 0
                    ? "Всем файлам возвращены исходные имена. Профиль не изменён."
                    : "Часть файлов вернуть не удалось:\n\n" + string.Join(Environment.NewLine, errors.Take(15)),
                Title, MessageBoxButton.OK, errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            DialogResult = false;
        }

        void Finish(NextAction next)
        {
            var profile = new Profile
            {
                AutoApply = _autoApply.IsChecked == true,
                IconFix = _iconFix.IsChecked == true,
                PreloadShared = _preload.IsChecked == true,
                PreloadExclude = _preloadExclude
            };

            // Сохраняем отключённые плагины, которых сейчас нет на диске (например, временно удалённые)
            var scanned = new HashSet<string>(_rows.Select(r => r.Group.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var key in PluginStore.LoadProfile().Disabled)
                if (!scanned.Contains(key)) profile.Disabled.Add(key);

            foreach (var r in _rows)
                if (r.Box.IsChecked != true) profile.Disabled.Add(r.Group.Key);

            try
            {
                PluginStore.SaveProfile(profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить профиль:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_ghLoaded)
            {
                MessageBox.Show(this,
                    "Профиль сохранён. Изменения применятся только после перезапуска Revit.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var errors = PluginStore.Apply(profile, out _);
                if (errors.Count > 0)
                {
                    var text = string.Join(Environment.NewLine, errors.Take(15));
                    if (errors.Count > 15) text += Environment.NewLine + $"... и ещё {errors.Count - 15} (см. лог в папке logs)";
                    foreach (var e in errors) PluginStore.Log("Apply: " + e);
                    MessageBox.Show(this, "Часть файлов обработать не удалось:\n\n" + text,
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            Next = next;
            DialogResult = true;
        }
    }
}
