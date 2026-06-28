using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SwellSSH.Services;
using Windows.UI;

namespace SwellSSH.Terminal
{
    public sealed partial class TerminalView : UserControl
    {
        private TerminalSession? _session;
        private CanvasTextFormat? _textFormat;
        private double _charWidth = 9;
        private double _charHeight = 18;
        private int _scrollOffset = 0;

        // Selection state
        private (int x, int y)? _selectionStart;
        private (int x, int y)? _selectionEnd;
        private bool _isSelecting;
        private volatile bool _isLoaded;
        private char? _pendingHighSurrogate;
        private int _needsRedraw;
        private int _redrawScheduled;
        private int _redrawGeneration;
        private bool _hasConnected; // true after the first real-size connection
        private RowRenderCache[] _rowCache = Array.Empty<RowRenderCache>();
        private TerminalRow?[] _visibleRows = Array.Empty<TerminalRow?>();
        private long[] _visibleVersions = Array.Empty<long>();
        private int _rowRenderGeneration;

        private sealed class RowRenderCache : IDisposable
        {
            public TerminalRow? Source;
            public long Version = -1;
            public int Columns;
            public int SelectionKey;
            public int Generation;
            public CanvasCommandList? Commands;

            public void Dispose()
            {
                Commands?.Dispose();
                Commands = null;
                Source = null;
                Version = -1;
            }
        }

        // Colors
        private Color _defaultBg = Color.FromArgb(255, 12, 12, 12);
        private Color _defaultFg = Color.FromArgb(255, 204, 204, 204);
        private Color _selectionBg = Color.FromArgb(255, 38, 79, 120);
        private Color _cursorColor = Color.FromArgb(255, 204, 204, 204);
        
        // Light theme ANSI colors — all tuned for contrast on #FAFAFA background
        private static readonly Color[] LightStandardColors = new Color[16]
        {
            Color.FromArgb(255,  12,  12,  12),  // 0  Black        → near-black (was #F2F2F2, invisible!)
            Color.FromArgb(255, 175,  20,  20),  // 1  Red          → dark red
            Color.FromArgb(255,   0, 130,   0),  // 2  Green        → dark green
            Color.FromArgb(255, 150, 110,   0),  // 3  Yellow       → dark amber/olive
            Color.FromArgb(255,   0,  55, 200),  // 4  Blue         → medium-dark blue
            Color.FromArgb(255, 130,  15, 145),  // 5  Magenta      → dark magenta
            Color.FromArgb(255,   0, 130, 145),  // 6  Cyan         → dark teal
            Color.FromArgb(255, 200, 200, 200),  // 7  White        → light grey (bg placeholder)
            Color.FromArgb(255,  90,  90,  90),  // 8  Bright Black → medium grey
            Color.FromArgb(255, 205,  40,  40),  // 9  Bright Red   → vivid red, readable
            Color.FromArgb(255,   0, 155,   0),  // 10 Bright Green → vivid green, readable
            Color.FromArgb(255, 160, 120,   0),  // 11 Bright Yellow → dark gold (was #F9F1A5, invisible!)
            Color.FromArgb(255,  30,  90, 210),  // 12 Bright Blue  → vivid blue
            Color.FromArgb(255, 160,  20, 170),  // 13 Bright Magenta → vivid purple
            Color.FromArgb(255,   0, 155, 165),  // 14 Bright Cyan  → vivid teal (was #61D6D6, low contrast)
            Color.FromArgb(255,  40,  40,  40)   // 15 Bright White → dark grey (light bg inverted)
        };

        // Dark theme ANSI colors
        private static readonly Color[] DarkStandardColors = new Color[16]
        {
            Color.FromArgb(255, 12, 12, 12),     // 0 Black
            Color.FromArgb(255, 197, 15, 31),    // 1 Red
            Color.FromArgb(255, 19, 161, 14),    // 2 Green
            Color.FromArgb(255, 193, 156, 0),    // 3 Yellow
            Color.FromArgb(255, 0, 55, 218),     // 4 Blue
            Color.FromArgb(255, 136, 23, 152),   // 5 Magenta
            Color.FromArgb(255, 58, 150, 221),   // 6 Cyan
            Color.FromArgb(255, 204, 204, 204),  // 7 White
            Color.FromArgb(255, 118, 118, 118),  // 8 Bright Black
            Color.FromArgb(255, 231, 72, 86),    // 9 Bright Red
            Color.FromArgb(255, 22, 198, 12),    // 10 Bright Green
            Color.FromArgb(255, 249, 241, 165),  // 11 Bright Yellow
            Color.FromArgb(255, 59, 120, 255),   // 12 Bright Blue
            Color.FromArgb(255, 180, 0, 158),    // 13 Bright Magenta
            Color.FromArgb(255, 97, 214, 214),   // 14 Bright Cyan
            Color.FromArgb(255, 242, 242, 242)   // 15 Bright White
        };

        // Termark Dark
        private static readonly Color[] TermarkDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x19, 0x19, 0x19), Color.FromArgb(255, 0xff, 0x7b, 0x72), Color.FromArgb(255, 0x7e, 0xe7, 0x87), Color.FromArgb(255, 0xe3, 0xb3, 0x41),
            Color.FromArgb(255, 0x79, 0xc0, 0xff), Color.FromArgb(255, 0xd2, 0xa8, 0xff), Color.FromArgb(255, 0x39, 0xc5, 0xcf), Color.FromArgb(255, 0xb1, 0xba, 0xc4),
            Color.FromArgb(255, 0x7a, 0x7a, 0x77), Color.FromArgb(255, 0xff, 0xa1, 0x98), Color.FromArgb(255, 0x56, 0xd3, 0x64), Color.FromArgb(255, 0xf2, 0xcc, 0x60),
            Color.FromArgb(255, 0xa5, 0xd6, 0xff), Color.FromArgb(255, 0xe2, 0xc5, 0xff), Color.FromArgb(255, 0x56, 0xd4, 0xdd), Color.FromArgb(255, 0xf0, 0xf6, 0xfc)
        };
        // Flexoki Dark
        private static readonly Color[] FlexokiDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x10, 0x0f, 0x0f), Color.FromArgb(255, 0xaf, 0x30, 0x29), Color.FromArgb(255, 0x66, 0x80, 0x0b), Color.FromArgb(255, 0xad, 0x83, 0x01),
            Color.FromArgb(255, 0x20, 0x5e, 0xa6), Color.FromArgb(255, 0x5e, 0x40, 0x9d), Color.FromArgb(255, 0x24, 0x83, 0x7b), Color.FromArgb(255, 0xce, 0xcd, 0xc3),
            Color.FromArgb(255, 0x28, 0x27, 0x26), Color.FromArgb(255, 0xd1, 0x4d, 0x41), Color.FromArgb(255, 0x87, 0x9a, 0x39), Color.FromArgb(255, 0xd0, 0xa2, 0x15),
            Color.FromArgb(255, 0x43, 0x85, 0xbe), Color.FromArgb(255, 0x8b, 0x7e, 0xc8), Color.FromArgb(255, 0x3a, 0xa9, 0x9f), Color.FromArgb(255, 0xff, 0xfc, 0xf0)
        };
        // Kanagawa Wave
        private static readonly Color[] KanagawaWaveColors = new Color[16]
        {
            Color.FromArgb(255, 0x09, 0x06, 0x18), Color.FromArgb(255, 0xc3, 0x40, 0x43), Color.FromArgb(255, 0x76, 0x94, 0x6a), Color.FromArgb(255, 0xc0, 0xa3, 0x6e),
            Color.FromArgb(255, 0x7e, 0x9c, 0xd8), Color.FromArgb(255, 0x95, 0x7f, 0xb8), Color.FromArgb(255, 0x6a, 0x95, 0x89), Color.FromArgb(255, 0xc8, 0xc0, 0x93),
            Color.FromArgb(255, 0x72, 0x71, 0x69), Color.FromArgb(255, 0xe8, 0x24, 0x24), Color.FromArgb(255, 0x98, 0xbb, 0x6c), Color.FromArgb(255, 0xe6, 0xc3, 0x84),
            Color.FromArgb(255, 0x7f, 0xb4, 0xca), Color.FromArgb(255, 0x93, 0x8a, 0xa9), Color.FromArgb(255, 0x7a, 0xa8, 0x9f), Color.FromArgb(255, 0xdc, 0xd7, 0xba)
        };
        // Night Owl
        private static readonly Color[] NightOwlColors = new Color[16]
        {
            Color.FromArgb(255, 0x01, 0x16, 0x27), Color.FromArgb(255, 0xef, 0x53, 0x50), Color.FromArgb(255, 0x22, 0xda, 0x6e), Color.FromArgb(255, 0xad, 0xdb, 0x67),
            Color.FromArgb(255, 0x82, 0xaa, 0xff), Color.FromArgb(255, 0xc7, 0x92, 0xea), Color.FromArgb(255, 0x21, 0xc7, 0xa8), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x57, 0x56, 0x56), Color.FromArgb(255, 0xef, 0x53, 0x50), Color.FromArgb(255, 0x22, 0xda, 0x6e), Color.FromArgb(255, 0xff, 0xeb, 0x95),
            Color.FromArgb(255, 0x82, 0xaa, 0xff), Color.FromArgb(255, 0xc7, 0x92, 0xea), Color.FromArgb(255, 0x7f, 0xdb, 0xca), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Hacker Green
        private static readonly Color[] HackerGreenColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xff, 0x00, 0x00), Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0x00, 0x8f, 0x11),
            Color.FromArgb(255, 0x00, 0x5f, 0x00), Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0x00, 0xff, 0x41),
            Color.FromArgb(255, 0x00, 0x11, 0x00), Color.FromArgb(255, 0xff, 0x00, 0x00), Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0x00, 0xff, 0x41),
            Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0x00, 0xff, 0x41), Color.FromArgb(255, 0xcc, 0xff, 0xcc)
        };
        // Cyberpunk
        private static readonly Color[] CyberpunkColors = new Color[16]
        {
            Color.FromArgb(255, 0x0d, 0x02, 0x21), Color.FromArgb(255, 0xff, 0x00, 0x6e), Color.FromArgb(255, 0x83, 0x38, 0xec), Color.FromArgb(255, 0xff, 0xbe, 0x0b),
            Color.FromArgb(255, 0x3a, 0x86, 0xff), Color.FromArgb(255, 0xff, 0x00, 0x6e), Color.FromArgb(255, 0x06, 0xff, 0xa5), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x6c, 0x5b, 0x7b), Color.FromArgb(255, 0xff, 0x00, 0x6e), Color.FromArgb(255, 0x83, 0x38, 0xec), Color.FromArgb(255, 0xff, 0xbe, 0x0b),
            Color.FromArgb(255, 0x3a, 0x86, 0xff), Color.FromArgb(255, 0xff, 0x00, 0x6e), Color.FromArgb(255, 0x06, 0xff, 0xa5), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Cobalt2
        private static readonly Color[] Cobalt2Colors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xff, 0x5d, 0x38), Color.FromArgb(255, 0x3a, 0xd9, 0x00), Color.FromArgb(255, 0xff, 0xb4, 0x02),
            Color.FromArgb(255, 0x00, 0x88, 0xff), Color.FromArgb(255, 0xbc, 0x3f, 0xbc), Color.FromArgb(255, 0x00, 0xd0, 0xd0), Color.FromArgb(255, 0xd0, 0xd0, 0xd0),
            Color.FromArgb(255, 0x80, 0x80, 0x80), Color.FromArgb(255, 0xff, 0x80, 0x70), Color.FromArgb(255, 0x66, 0xff, 0x00), Color.FromArgb(255, 0xff, 0xeb, 0x3b),
            Color.FromArgb(255, 0x5b, 0xa3, 0xff), Color.FromArgb(255, 0xff, 0x4e, 0xff), Color.FromArgb(255, 0x5c, 0xe8, 0xe8), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Rose Pine
        private static readonly Color[] RosePineColors = new Color[16]
        {
            Color.FromArgb(255, 0x19, 0x17, 0x24), Color.FromArgb(255, 0xeb, 0x6f, 0x92), Color.FromArgb(255, 0x31, 0x74, 0x8f), Color.FromArgb(255, 0xf6, 0xc1, 0x77),
            Color.FromArgb(255, 0x9c, 0xcf, 0xd8), Color.FromArgb(255, 0xc4, 0xa7, 0xe7), Color.FromArgb(255, 0xeb, 0xbc, 0xba), Color.FromArgb(255, 0xe0, 0xde, 0xf4),
            Color.FromArgb(255, 0x55, 0x51, 0x69), Color.FromArgb(255, 0xeb, 0x6f, 0x92), Color.FromArgb(255, 0x31, 0x74, 0x8f), Color.FromArgb(255, 0xf6, 0xc1, 0x77),
            Color.FromArgb(255, 0x9c, 0xcf, 0xd8), Color.FromArgb(255, 0xc4, 0xa7, 0xe7), Color.FromArgb(255, 0xeb, 0xbc, 0xba), Color.FromArgb(255, 0xe0, 0xde, 0xf4)
        };
        // Catppuccin Mocha
        private static readonly Color[] CatppuccinMochaColors = new Color[16]
        {
            Color.FromArgb(255, 0x45, 0x47, 0x5a), Color.FromArgb(255, 0xf3, 0x8b, 0xa8), Color.FromArgb(255, 0xa6, 0xe3, 0xa1), Color.FromArgb(255, 0xf9, 0xe2, 0xaf),
            Color.FromArgb(255, 0x89, 0xb4, 0xfa), Color.FromArgb(255, 0xf5, 0xc2, 0xde), Color.FromArgb(255, 0x94, 0xe2, 0xd5), Color.FromArgb(255, 0xcd, 0xd6, 0xf4),
            Color.FromArgb(255, 0x58, 0x5b, 0x70), Color.FromArgb(255, 0xf3, 0x8b, 0xa8), Color.FromArgb(255, 0xa6, 0xe3, 0xa1), Color.FromArgb(255, 0xf9, 0xe2, 0xaf),
            Color.FromArgb(255, 0x89, 0xb4, 0xfa), Color.FromArgb(255, 0xf5, 0xc2, 0xde), Color.FromArgb(255, 0x94, 0xe2, 0xd5), Color.FromArgb(255, 0xcd, 0xd6, 0xf4)
        };
        // Tokyo Night
        private static readonly Color[] TokyoNightColors = new Color[16]
        {
            Color.FromArgb(255, 0x15, 0x20, 0x2b), Color.FromArgb(255, 0xf7, 0x76, 0x8e), Color.FromArgb(255, 0x9e, 0xce, 0x6a), Color.FromArgb(255, 0xe0, 0xaf, 0x68),
            Color.FromArgb(255, 0x7a, 0xa2, 0xf7), Color.FromArgb(255, 0xbb, 0x9a, 0xf7), Color.FromArgb(255, 0x7d, 0xcf, 0xff), Color.FromArgb(255, 0xc0, 0xca, 0xf5),
            Color.FromArgb(255, 0x41, 0x48, 0x68), Color.FromArgb(255, 0xf7, 0x76, 0x8e), Color.FromArgb(255, 0x9e, 0xce, 0x6a), Color.FromArgb(255, 0xe0, 0xaf, 0x68),
            Color.FromArgb(255, 0x7a, 0xa2, 0xf7), Color.FromArgb(255, 0xbb, 0x9a, 0xf7), Color.FromArgb(255, 0x7d, 0xcf, 0xff), Color.FromArgb(255, 0xc0, 0xca, 0xf5)
        };
        // Gruvbox Dark
        private static readonly Color[] GruvboxDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x28, 0x28, 0x28), Color.FromArgb(255, 0xcc, 0x24, 0x1d), Color.FromArgb(255, 0x98, 0x97, 0x1a), Color.FromArgb(255, 0xd7, 0x99, 0x21),
            Color.FromArgb(255, 0x45, 0x85, 0x88), Color.FromArgb(255, 0xb1, 0x62, 0x86), Color.FromArgb(255, 0x68, 0x9d, 0x6a), Color.FromArgb(255, 0xa8, 0x99, 0x84),
            Color.FromArgb(255, 0x92, 0x83, 0x74), Color.FromArgb(255, 0xfb, 0x49, 0x34), Color.FromArgb(255, 0xb8, 0xbb, 0x26), Color.FromArgb(255, 0xfa, 0xbd, 0x2f),
            Color.FromArgb(255, 0x83, 0xa5, 0x98), Color.FromArgb(255, 0xd3, 0x86, 0x9b), Color.FromArgb(255, 0x8e, 0xc0, 0x7c), Color.FromArgb(255, 0xeb, 0xdb, 0xb2)
        };
        // Green Screen (Phosphor Display)
        private static readonly Color[] GreenScreenColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0x21, 0xb5, 0x68), Color.FromArgb(255, 0x21, 0xb5, 0x68), Color.FromArgb(255, 0x21, 0xb5, 0x68),
            Color.FromArgb(255, 0x00, 0xaa, 0xff), Color.FromArgb(255, 0xff, 0x69, 0xb4), Color.FromArgb(255, 0x21, 0xb5, 0x68), Color.FromArgb(255, 0x21, 0xb5, 0x68),
            Color.FromArgb(255, 0x00, 0x33, 0x00), Color.FromArgb(255, 0x21, 0xb5, 0x68), Color.FromArgb(255, 0x21, 0xb5, 0x68), Color.FromArgb(255, 0x21, 0xb5, 0x68),
            Color.FromArgb(255, 0x00, 0xaa, 0xff), Color.FromArgb(255, 0xff, 0x69, 0xb4), Color.FromArgb(255, 0x21, 0xb5, 0x68), Color.FromArgb(255, 0x21, 0xb5, 0x68)
        };
        // Ayu Dark
        private static readonly Color[] AyuDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xff, 0x33, 0x33), Color.FromArgb(255, 0x86, 0xb3, 0x00), Color.FromArgb(255, 0xff, 0xb4, 0x54),
            Color.FromArgb(255, 0x36, 0xa3, 0xd9), Color.FromArgb(255, 0xf0, 0x71, 0x78), Color.FromArgb(255, 0x95, 0xe1, 0xd3), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x32, 0x32, 0x32), Color.FromArgb(255, 0xff, 0x65, 0x65), Color.FromArgb(255, 0xb8, 0xe5, 0x36), Color.FromArgb(255, 0xff, 0xc6, 0x6d),
            Color.FromArgb(255, 0x55, 0xb4, 0xd4), Color.FromArgb(255, 0xff, 0x88, 0x88), Color.FromArgb(255, 0x95, 0xe1, 0xd3), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Material Dark
        private static readonly Color[] MaterialDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xf0, 0x71, 0x78), Color.FromArgb(255, 0xc3, 0xe8, 0x8d), Color.FromArgb(255, 0xff, 0xcb, 0x8b),
            Color.FromArgb(255, 0x82, 0xb1, 0xff), Color.FromArgb(255, 0xc7, 0x92, 0xea), Color.FromArgb(255, 0x89, 0xdd, 0xff), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x54, 0x6e, 0x7a), Color.FromArgb(255, 0xef, 0x53, 0x50), Color.FromArgb(255, 0x9c, 0xcc, 0x65), Color.FromArgb(255, 0xff, 0xeb, 0x3b),
            Color.FromArgb(255, 0x42, 0xa5, 0xf5), Color.FromArgb(255, 0xab, 0x47, 0xbc), Color.FromArgb(255, 0x29, 0xb6, 0xf6), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Atom One Dark
        private static readonly Color[] AtomOneDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x1e, 0x1e, 0x1e), Color.FromArgb(255, 0xe0, 0x6c, 0x75), Color.FromArgb(255, 0x98, 0xc3, 0x79), Color.FromArgb(255, 0xd1, 0x9a, 0x66),
            Color.FromArgb(255, 0x61, 0xaf, 0xef), Color.FromArgb(255, 0xc6, 0x78, 0xdd), Color.FromArgb(255, 0x56, 0xb6, 0xc2), Color.FromArgb(255, 0xab, 0xb2, 0xbf),
            Color.FromArgb(255, 0x5c, 0x63, 0x70), Color.FromArgb(255, 0xe0, 0x6c, 0x75), Color.FromArgb(255, 0x98, 0xc3, 0x79), Color.FromArgb(255, 0xd1, 0x9a, 0x66),
            Color.FromArgb(255, 0x61, 0xaf, 0xef), Color.FromArgb(255, 0xc6, 0x78, 0xdd), Color.FromArgb(255, 0x56, 0xb6, 0xc2), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Solarized Dark
        private static readonly Color[] SolarizedDarkColors = new Color[16]
        {
            Color.FromArgb(255, 0x07, 0x36, 0x42), Color.FromArgb(255, 0xdc, 0x32, 0x2f), Color.FromArgb(255, 0x85, 0x99, 0x00), Color.FromArgb(255, 0xb5, 0x89, 0x00),
            Color.FromArgb(255, 0x26, 0x8b, 0xd2), Color.FromArgb(255, 0xd3, 0x36, 0x82), Color.FromArgb(255, 0x2a, 0xa1, 0x98), Color.FromArgb(255, 0xee, 0xe8, 0xd5),
            Color.FromArgb(255, 0x00, 0x2b, 0x36), Color.FromArgb(255, 0xcb, 0x4b, 0x16), Color.FromArgb(255, 0x58, 0x6e, 0x75), Color.FromArgb(255, 0x65, 0x7b, 0x83),
            Color.FromArgb(255, 0x83, 0x94, 0x96), Color.FromArgb(255, 0x6c, 0x71, 0xc4), Color.FromArgb(255, 0x93, 0xa1, 0xa1), Color.FromArgb(255, 0xfd, 0xf6, 0xe3)
        };
        // Dracula
        private static readonly Color[] DraculaColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xff, 0x55, 0x55), Color.FromArgb(255, 0x50, 0xfa, 0x7b), Color.FromArgb(255, 0xf1, 0xfa, 0x8c),
            Color.FromArgb(255, 0x61, 0xbf, 0xff), Color.FromArgb(255, 0xff, 0x79, 0xc6), Color.FromArgb(255, 0x8b, 0xe9, 0xfd), Color.FromArgb(255, 0xbf, 0xbf, 0xbf),
            Color.FromArgb(255, 0x57, 0x5b, 0x86), Color.FromArgb(255, 0xff, 0x6e, 0x6e), Color.FromArgb(255, 0x69, 0xff, 0x94), Color.FromArgb(255, 0xff, 0xff, 0xa5),
            Color.FromArgb(255, 0x8b, 0xe9, 0xfd), Color.FromArgb(255, 0xff, 0x92, 0xdf), Color.FromArgb(255, 0xa4, 0xff, 0xff), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };
        // Monokai
        private static readonly Color[] MonokaiColors = new Color[16]
        {
            Color.FromArgb(255, 0x27, 0x28, 0x22), Color.FromArgb(255, 0xf9, 0x26, 0x72), Color.FromArgb(255, 0xa6, 0xe2, 0x2e), Color.FromArgb(255, 0xe6, 0xdb, 0x74),
            Color.FromArgb(255, 0x66, 0xd9, 0xef), Color.FromArgb(255, 0xae, 0x81, 0xff), Color.FromArgb(255, 0xa1, 0xef, 0xe4), Color.FromArgb(255, 0xf8, 0xf8, 0xf2),
            Color.FromArgb(255, 0x75, 0x71, 0x5e), Color.FromArgb(255, 0xf9, 0x26, 0x72), Color.FromArgb(255, 0xa6, 0xe2, 0x2e), Color.FromArgb(255, 0xe6, 0xdb, 0x74),
            Color.FromArgb(255, 0x66, 0xd9, 0xef), Color.FromArgb(255, 0xae, 0x81, 0xff), Color.FromArgb(255, 0xa1, 0xef, 0xe4), Color.FromArgb(255, 0xf9, 0xf8, 0xf5)
        };
        // Nord
        private static readonly Color[] NordColors = new Color[16]
        {
            Color.FromArgb(255, 0x3b, 0x42, 0x52), Color.FromArgb(255, 0xbf, 0x61, 0x6a), Color.FromArgb(255, 0xa3, 0xbe, 0x8c), Color.FromArgb(255, 0xeb, 0xcb, 0x8b),
            Color.FromArgb(255, 0x81, 0xa1, 0xc1), Color.FromArgb(255, 0xb4, 0x8e, 0xad), Color.FromArgb(255, 0x88, 0xc0, 0xd0), Color.FromArgb(255, 0xd8, 0xde, 0xe9),
            Color.FromArgb(255, 0x4c, 0x56, 0x6a), Color.FromArgb(255, 0xbf, 0x61, 0x6a), Color.FromArgb(255, 0xa3, 0xbe, 0x8c), Color.FromArgb(255, 0xeb, 0xcb, 0x8b),
            Color.FromArgb(255, 0x81, 0xa1, 0xc1), Color.FromArgb(255, 0xb4, 0x8e, 0xad), Color.FromArgb(255, 0x88, 0xc0, 0xd0), Color.FromArgb(255, 0xec, 0xef, 0xf4)
        };

// Termark Light
        private static readonly Color[] TermarkLightColors = new Color[16]
        {
            Color.FromArgb(255, 0x24, 0x29, 0x2f), Color.FromArgb(255, 0xcf, 0x22, 0x2e), Color.FromArgb(255, 0x11, 0x63, 0x29), Color.FromArgb(255, 0x9a, 0x67, 0x00),
            Color.FromArgb(255, 0x09, 0x69, 0xda), Color.FromArgb(255, 0x82, 0x50, 0xdf), Color.FromArgb(255, 0x1b, 0x7c, 0x83), Color.FromArgb(255, 0x6e, 0x77, 0x81),
            Color.FromArgb(255, 0x24, 0x29, 0x2f), Color.FromArgb(255, 0xcf, 0x22, 0x2e), Color.FromArgb(255, 0x11, 0x63, 0x29), Color.FromArgb(255, 0x9a, 0x67, 0x00),
            Color.FromArgb(255, 0x09, 0x69, 0xda), Color.FromArgb(255, 0x82, 0x50, 0xdf), Color.FromArgb(255, 0x1b, 0x7c, 0x83), Color.FromArgb(255, 0x6e, 0x77, 0x81)
        };

        // Rose Pine Dawn
        private static readonly Color[] RosePineDawnColors = new Color[16]
        {
            Color.FromArgb(255, 0x55, 0x51, 0x69), Color.FromArgb(255, 0xb4, 0x63, 0x7a), Color.FromArgb(255, 0x28, 0x69, 0x83), Color.FromArgb(255, 0xd7, 0xaf, 0x70),
            Color.FromArgb(255, 0x56, 0x94, 0x9f), Color.FromArgb(255, 0x90, 0x7a, 0xa9), Color.FromArgb(255, 0xea, 0x9d, 0x34), Color.FromArgb(255, 0xfa, 0xf4, 0xed),
            Color.FromArgb(255, 0x55, 0x51, 0x69), Color.FromArgb(255, 0xb4, 0x63, 0x7a), Color.FromArgb(255, 0x28, 0x69, 0x83), Color.FromArgb(255, 0xd7, 0xaf, 0x70),
            Color.FromArgb(255, 0x56, 0x94, 0x9f), Color.FromArgb(255, 0x90, 0x7a, 0xa9), Color.FromArgb(255, 0xea, 0x9d, 0x34), Color.FromArgb(255, 0xfa, 0xf4, 0xed)
        };

        // Catppuccin Latte
        private static readonly Color[] CatppuccinLatteColors = new Color[16]
        {
            Color.FromArgb(255, 0x5c, 0x5f, 0x77), Color.FromArgb(255, 0xd2, 0x0f, 0x39), Color.FromArgb(255, 0x40, 0xa0, 0x2b), Color.FromArgb(255, 0xdf, 0x8e, 0x1d),
            Color.FromArgb(255, 0x1e, 0x66, 0xf5), Color.FromArgb(255, 0xea, 0x76, 0xcb), Color.FromArgb(255, 0x04, 0xa5, 0xe5), Color.FromArgb(255, 0xef, 0xf1, 0xf5),
            Color.FromArgb(255, 0x5c, 0x5f, 0x77), Color.FromArgb(255, 0xd2, 0x0f, 0x39), Color.FromArgb(255, 0x40, 0xa0, 0x2b), Color.FromArgb(255, 0xdf, 0x8e, 0x1d),
            Color.FromArgb(255, 0x1e, 0x66, 0xf5), Color.FromArgb(255, 0xea, 0x76, 0xcb), Color.FromArgb(255, 0x04, 0xa5, 0xe5), Color.FromArgb(255, 0xef, 0xf1, 0xf5)
        };

        // Tokyo Day
        private static readonly Color[] TokyoDayColors = new Color[16]
        {
            Color.FromArgb(255, 0x1f, 0x23, 0x35), Color.FromArgb(255, 0xf5, 0x2a, 0x65), Color.FromArgb(255, 0x58, 0x75, 0x39), Color.FromArgb(255, 0x8e, 0x8a, 0x2b),
            Color.FromArgb(255, 0x18, 0x80, 0x92), Color.FromArgb(255, 0x8c, 0x6c, 0xb8), Color.FromArgb(255, 0x00, 0x71, 0x97), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x1f, 0x23, 0x35), Color.FromArgb(255, 0xf5, 0x2a, 0x65), Color.FromArgb(255, 0x58, 0x75, 0x39), Color.FromArgb(255, 0x8e, 0x8a, 0x2b),
            Color.FromArgb(255, 0x18, 0x80, 0x92), Color.FromArgb(255, 0x8c, 0x6c, 0xb8), Color.FromArgb(255, 0x00, 0x71, 0x97), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };

        // Gruvbox Light
        private static readonly Color[] GruvboxLightColors = new Color[16]
        {
            Color.FromArgb(255, 0xfb, 0xf1, 0xc7), Color.FromArgb(255, 0xcc, 0x24, 0x1d), Color.FromArgb(255, 0x98, 0x97, 0x1a), Color.FromArgb(255, 0xd7, 0x99, 0x21),
            Color.FromArgb(255, 0x45, 0x85, 0x88), Color.FromArgb(255, 0xb1, 0x62, 0x86), Color.FromArgb(255, 0x68, 0x9d, 0x6a), Color.FromArgb(255, 0x7c, 0x6f, 0x64),
            Color.FromArgb(255, 0xfb, 0xf1, 0xc7), Color.FromArgb(255, 0xcc, 0x24, 0x1d), Color.FromArgb(255, 0x98, 0x97, 0x1a), Color.FromArgb(255, 0xd7, 0x99, 0x21),
            Color.FromArgb(255, 0x45, 0x85, 0x88), Color.FromArgb(255, 0xb1, 0x62, 0x86), Color.FromArgb(255, 0x68, 0x9d, 0x6a), Color.FromArgb(255, 0x7c, 0x6f, 0x64)
        };

        // Ayu Light
        private static readonly Color[] AyuLightColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xff, 0x33, 0x33), Color.FromArgb(255, 0x86, 0xb3, 0x00), Color.FromArgb(255, 0xff, 0xb4, 0x54),
            Color.FromArgb(255, 0x36, 0xa3, 0xd9), Color.FromArgb(255, 0xf0, 0x71, 0x78), Color.FromArgb(255, 0x4d, 0xbf, 0x99), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xff, 0x33, 0x33), Color.FromArgb(255, 0x86, 0xb3, 0x00), Color.FromArgb(255, 0xff, 0xb4, 0x54),
            Color.FromArgb(255, 0x36, 0xa3, 0xd9), Color.FromArgb(255, 0xf0, 0x71, 0x78), Color.FromArgb(255, 0x4d, 0xbf, 0x99), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };

        // Material Light
        private static readonly Color[] MaterialLightColors = new Color[16]
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xe5, 0x39, 0x35), Color.FromArgb(255, 0x43, 0xa0, 0x47), Color.FromArgb(255, 0xfb, 0x8c, 0x00),
            Color.FromArgb(255, 0x1e, 0x88, 0xe5), Color.FromArgb(255, 0x8e, 0x24, 0xaa), Color.FromArgb(255, 0x00, 0xac, 0xc1), Color.FromArgb(255, 0xff, 0xff, 0xff),
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xe5, 0x39, 0x35), Color.FromArgb(255, 0x43, 0xa0, 0x47), Color.FromArgb(255, 0xfb, 0x8c, 0x00),
            Color.FromArgb(255, 0x1e, 0x88, 0xe5), Color.FromArgb(255, 0x8e, 0x24, 0xaa), Color.FromArgb(255, 0x00, 0xac, 0xc1), Color.FromArgb(255, 0xff, 0xff, 0xff)
        };

        // Atom One Light
        private static readonly Color[] AtomOneLightColors = new Color[16]
        {
            Color.FromArgb(255, 0x38, 0x3a, 0x42), Color.FromArgb(255, 0xe4, 0x56, 0x49), Color.FromArgb(255, 0x50, 0xa1, 0x4f), Color.FromArgb(255, 0xc1, 0x84, 0x01),
            Color.FromArgb(255, 0x40, 0x78, 0xf2), Color.FromArgb(255, 0xa6, 0x26, 0xa4), Color.FromArgb(255, 0x01, 0x84, 0xbc), Color.FromArgb(255, 0xfa, 0xfa, 0xfa),
            Color.FromArgb(255, 0x38, 0x3a, 0x42), Color.FromArgb(255, 0xe4, 0x56, 0x49), Color.FromArgb(255, 0x50, 0xa1, 0x4f), Color.FromArgb(255, 0xc1, 0x84, 0x01),
            Color.FromArgb(255, 0x40, 0x78, 0xf2), Color.FromArgb(255, 0xa6, 0x26, 0xa4), Color.FromArgb(255, 0x01, 0x84, 0xbc), Color.FromArgb(255, 0xfa, 0xfa, 0xfa)
        };

        // Solarized Light
        private static readonly Color[] SolarizedLightColors = new Color[16]
        {
            Color.FromArgb(255, 0x07, 0x36, 0x42), Color.FromArgb(255, 0xdc, 0x32, 0x2f), Color.FromArgb(255, 0x85, 0x99, 0x00), Color.FromArgb(255, 0xb5, 0x89, 0x00),
            Color.FromArgb(255, 0x26, 0x8b, 0xd2), Color.FromArgb(255, 0xd3, 0x36, 0x82), Color.FromArgb(255, 0x2a, 0xa1, 0x98), Color.FromArgb(255, 0xfd, 0xf6, 0xe3),
            Color.FromArgb(255, 0x07, 0x36, 0x42), Color.FromArgb(255, 0xdc, 0x32, 0x2f), Color.FromArgb(255, 0x85, 0x99, 0x00), Color.FromArgb(255, 0xb5, 0x89, 0x00),
            Color.FromArgb(255, 0x26, 0x8b, 0xd2), Color.FromArgb(255, 0xd3, 0x36, 0x82), Color.FromArgb(255, 0x2a, 0xa1, 0x98), Color.FromArgb(255, 0xfd, 0xf6, 0xe3)
        };

        private Color[] _ansiColors = DarkStandardColors;

        private Models.TerminalSettings _settings = new();

        public TerminalView()
        {
            this.InitializeComponent();

            // Handle keyboard input natively on this control
            this.IsTabStop = true;
            this.UseSystemFocusVisuals = false; // Disable default focus rect
            this.CharacterReceived += UIElement_CharacterReceived;
        }

        public void AttachSession(TerminalSession session)
        {
            if (_session != null)
            {
                _session.Buffer.BufferChanged -= RequestRedraw;
            }

            _session = session;
            _session.Buffer.BufferChanged += RequestRedraw;
            RequestRedraw();
            // Note: ConnectAsync is NOT called here.
            // It is called in Canvas_CreateResources after the canvas size is known,
            // so the initial SSH PTY size matches the actual pixel dimensions.
        }

        public void ApplySettings(Models.TerminalSettings settings)
        {
            _settings = settings;
            InvalidateRowCache();
            
            // JSON themes are the source of truth. The legacy mappings below remain
            // as a safe fallback for an invalid/missing configuration file.
            var configuredTheme = TerminalThemeService.Instance.Find(settings.ColorScheme);
            if (configuredTheme != null)
            {
                _defaultBg = TerminalThemeService.ParseColor(configuredTheme.Background, _defaultBg);
                _defaultFg = TerminalThemeService.ParseColor(configuredTheme.Foreground, _defaultFg);
                _selectionBg = TerminalThemeService.ParseColor(configuredTheme.SelectionBackground, _selectionBg);
                _cursorColor = TerminalThemeService.ParseColor(configuredTheme.CursorColor, _defaultFg);
                _ansiColors = configuredTheme.AnsiColors
                    .Select(c => TerminalThemeService.ParseColor(c, _defaultFg)).ToArray();
            }
            else if (settings.ColorScheme == "Default Light")
            {
                _defaultBg = Color.FromArgb(255, 250, 250, 250);
                _defaultFg = Color.FromArgb(255, 50, 50, 50);
                _ansiColors = LightStandardColors;
                _selectionBg = Color.FromArgb(255, 204, 232, 255);
                _cursorColor = Color.FromArgb(255, 50, 50, 50);
            }
            else if (settings.ColorScheme == "Termark Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x21, 0x21, 0x21);
                _defaultFg = Color.FromArgb(255, 0xe6, 0xed, 0xf3);
                _ansiColors = TermarkDarkColors;
                _selectionBg = Color.FromArgb(0x33, 0x92, 0xff, 0x44);
                _cursorColor = Color.FromArgb(255, 0xe6, 0xed, 0xf3);
            }
            else if (settings.ColorScheme == "Flexoki Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x10, 0x0f, 0x0f);
                _defaultFg = Color.FromArgb(255, 0xce, 0xcd, 0xc3);
                _ansiColors = FlexokiDarkColors;
                _selectionBg = Color.FromArgb(255, 0x28, 0x27, 0x26);
                _cursorColor = Color.FromArgb(255, 0xce, 0xcd, 0xc3);
            }
            else if (settings.ColorScheme == "Kanagawa Wave")
            {
                _defaultBg = Color.FromArgb(255, 0x1f, 0x1f, 0x28);
                _defaultFg = Color.FromArgb(255, 0xdc, 0xd7, 0xba);
                _ansiColors = KanagawaWaveColors;
                _selectionBg = Color.FromArgb(255, 0x2d, 0x4f, 0x67);
                _cursorColor = Color.FromArgb(255, 0xdc, 0xd7, 0xba);
            }
            else if (settings.ColorScheme == "Night Owl")
            {
                _defaultBg = Color.FromArgb(255, 0x01, 0x16, 0x27);
                _defaultFg = Color.FromArgb(255, 0xd6, 0xde, 0xeb);
                _ansiColors = NightOwlColors;
                _selectionBg = Color.FromArgb(255, 0x1d, 0x3b, 0x53);
                _cursorColor = Color.FromArgb(255, 0xd6, 0xde, 0xeb);
            }
            else if (settings.ColorScheme == "Hacker Green")
            {
                _defaultBg = Color.FromArgb(255, 0x0d, 0x02, 0x08);
                _defaultFg = Color.FromArgb(255, 0x00, 0xff, 0x41);
                _ansiColors = HackerGreenColors;
                _selectionBg = Color.FromArgb(255, 0x00, 0x3b, 0x00);
                _cursorColor = Color.FromArgb(255, 0x00, 0xff, 0x41);
            }
            else if (settings.ColorScheme == "Cyberpunk")
            {
                _defaultBg = Color.FromArgb(255, 0x0d, 0x02, 0x21);
                _defaultFg = Color.FromArgb(255, 0xff, 0x00, 0x6e);
                _ansiColors = CyberpunkColors;
                _selectionBg = Color.FromArgb(255, 0x3a, 0x0c, 0xa3);
                _cursorColor = Color.FromArgb(255, 0xff, 0x00, 0x6e);
            }
            else if (settings.ColorScheme == "Cobalt2")
            {
                _defaultBg = Color.FromArgb(255, 0x13, 0x27, 0x38);
                _defaultFg = Color.FromArgb(255, 0xff, 0xff, 0xff);
                _ansiColors = Cobalt2Colors;
                _selectionBg = Color.FromArgb(255, 0x1e, 0x3c, 0x41);
                _cursorColor = Color.FromArgb(255, 0xff, 0xbe, 0x0b);
            }
            else if (settings.ColorScheme == "Rose Pine")
            {
                _defaultBg = Color.FromArgb(255, 0x19, 0x17, 0x24);
                _defaultFg = Color.FromArgb(255, 0xe0, 0xde, 0xf4);
                _ansiColors = RosePineColors;
                _selectionBg = Color.FromArgb(255, 0x40, 0x3d, 0x52);
                _cursorColor = Color.FromArgb(255, 0xe0, 0xde, 0xf4);
            }
            else if (settings.ColorScheme == "Catppuccin Mocha")
            {
                _defaultBg = Color.FromArgb(255, 0x1e, 0x1e, 0x2e);
                _defaultFg = Color.FromArgb(255, 0xcd, 0xd6, 0xf4);
                _ansiColors = CatppuccinMochaColors;
                _selectionBg = Color.FromArgb(255, 0x31, 0x32, 0x44);
                _cursorColor = Color.FromArgb(255, 0xf5, 0xc2, 0xde);
            }
            else if (settings.ColorScheme == "Tokyo Night")
            {
                _defaultBg = Color.FromArgb(255, 0x1a, 0x1b, 0x26);
                _defaultFg = Color.FromArgb(255, 0xc0, 0xca, 0xf5);
                _ansiColors = TokyoNightColors;
                _selectionBg = Color.FromArgb(255, 0x33, 0x46, 0x7c);
                _cursorColor = Color.FromArgb(255, 0xc0, 0xca, 0xf5);
            }
            else if (settings.ColorScheme == "Gruvbox Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x28, 0x28, 0x28);
                _defaultFg = Color.FromArgb(255, 0xeb, 0xdb, 0xb2);
                _ansiColors = GruvboxDarkColors;
                _selectionBg = Color.FromArgb(255, 0x50, 0x49, 0x45);
                _cursorColor = Color.FromArgb(255, 0xeb, 0xdb, 0xb2);
            }
            else if (settings.ColorScheme == "Green Screen")
            {
                _defaultBg = Color.FromArgb(255, 0x0d, 0x11, 0x17);
                _defaultFg = Color.FromArgb(255, 0x21, 0xb5, 0x68);
                _ansiColors = GreenScreenColors;
                _selectionBg = Color.FromArgb(255, 0x00, 0x3b, 0x00);
                _cursorColor = Color.FromArgb(255, 0x21, 0xb5, 0x68);
            }
            else if (settings.ColorScheme == "Ayu Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x0f, 0x14, 0x19);
                _defaultFg = Color.FromArgb(255, 0xe6, 0xe1, 0xcf);
                _ansiColors = AyuDarkColors;
                _selectionBg = Color.FromArgb(255, 0x1f, 0x24, 0x30);
                _cursorColor = Color.FromArgb(255, 0xff, 0xb4, 0x54);
            }
            else if (settings.ColorScheme == "Material Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x26, 0x32, 0x38);
                _defaultFg = Color.FromArgb(255, 0xee, 0xff, 0xff);
                _ansiColors = MaterialDarkColors;
                _selectionBg = Color.FromArgb(255, 0x37, 0x47, 0x4f);
                _cursorColor = Color.FromArgb(255, 0xee, 0xff, 0xff);
            }
            else if (settings.ColorScheme == "Atom One Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x28, 0x2c, 0x34);
                _defaultFg = Color.FromArgb(255, 0xab, 0xb2, 0xbf);
                _ansiColors = AtomOneDarkColors;
                _selectionBg = Color.FromArgb(255, 0x3e, 0x44, 0x51);
                _cursorColor = Color.FromArgb(255, 0x56, 0xb6, 0xc2);
            }
            else if (settings.ColorScheme == "Solarized Dark")
            {
                _defaultBg = Color.FromArgb(255, 0x00, 0x2b, 0x36);
                _defaultFg = Color.FromArgb(255, 0x83, 0x94, 0x96);
                _ansiColors = SolarizedDarkColors;
                _selectionBg = Color.FromArgb(255, 0x07, 0x36, 0x42);
                _cursorColor = Color.FromArgb(255, 0x93, 0xa1, 0xa1);
            }
            else if (settings.ColorScheme == "Dracula")
            {
                _defaultBg = Color.FromArgb(255, 0x28, 0x2a, 0x36);
                _defaultFg = Color.FromArgb(255, 0xf8, 0xf8, 0xf2);
                _ansiColors = DraculaColors;
                _selectionBg = Color.FromArgb(255, 0x44, 0x47, 0x5a);
                _cursorColor = Color.FromArgb(255, 0xf8, 0xf8, 0xf2);
            }
            else if (settings.ColorScheme == "Monokai")
            {
                _defaultBg = Color.FromArgb(255, 0x27, 0x28, 0x22);
                _defaultFg = Color.FromArgb(255, 0xf8, 0xf8, 0xf2);
                _ansiColors = MonokaiColors;
                _selectionBg = Color.FromArgb(255, 0x49, 0x48, 0x3e);
                _cursorColor = Color.FromArgb(255, 0xf8, 0xf8, 0xf2);
            }
            else if (settings.ColorScheme == "Nord")
            {
                _defaultBg = Color.FromArgb(255, 0x2e, 0x34, 0x40);
                _defaultFg = Color.FromArgb(255, 0xd8, 0xde, 0xe9);
                _ansiColors = NordColors;
                _selectionBg = Color.FromArgb(255, 0x43, 0x4c, 0x5e);
                _cursorColor = Color.FromArgb(255, 0x88, 0xc0, 0xd0);
            }

else if (settings.ColorScheme == "Termark Light")
            {
                _defaultBg = Color.FromArgb(255, 0xff, 0xff, 0xff);
                _defaultFg = Color.FromArgb(255, 0x1f, 0x23, 0x28);
                _ansiColors = TermarkLightColors;
                _selectionBg = Color.FromArgb(0x33, 0x09, 0x69, 0xda);
                _cursorColor = Color.FromArgb(255, 0x1f, 0x23, 0x28);
            }
            else if (settings.ColorScheme == "Rose Pine Dawn")
            {
                _defaultBg = Color.FromArgb(255, 0xfa, 0xf4, 0xed);
                _defaultFg = Color.FromArgb(255, 0x57, 0x52, 0x79);
                _ansiColors = RosePineDawnColors;
                _selectionBg = Color.FromArgb(255, 0xf2, 0xe9, 0xe1);
                _cursorColor = Color.FromArgb(255, 0x57, 0x52, 0x79);
            }
            else if (settings.ColorScheme == "Catppuccin Latte")
            {
                _defaultBg = Color.FromArgb(255, 0xef, 0xf1, 0xf5);
                _defaultFg = Color.FromArgb(255, 0x4c, 0x4f, 0x69);
                _ansiColors = CatppuccinLatteColors;
                _selectionBg = Color.FromArgb(255, 0xe6, 0xe9, 0xef);
                _cursorColor = Color.FromArgb(255, 0xdc, 0x8a, 0x78);
            }
            else if (settings.ColorScheme == "Tokyo Day")
            {
                _defaultBg = Color.FromArgb(255, 0xff, 0xff, 0xff);
                _defaultFg = Color.FromArgb(255, 0x37, 0x60, 0xbf);
                _ansiColors = TokyoDayColors;
                _selectionBg = Color.FromArgb(255, 0xe5, 0xe1, 0xed);
                _cursorColor = Color.FromArgb(255, 0x37, 0x60, 0xbf);
            }
            else if (settings.ColorScheme == "Gruvbox Light")
            {
                _defaultBg = Color.FromArgb(255, 0xfb, 0xf1, 0xc7);
                _defaultFg = Color.FromArgb(255, 0x3c, 0x38, 0x36);
                _ansiColors = GruvboxLightColors;
                _selectionBg = Color.FromArgb(255, 0xf2, 0xe5, 0xbc);
                _cursorColor = Color.FromArgb(255, 0x3c, 0x38, 0x36);
            }
            else if (settings.ColorScheme == "Ayu Light")
            {
                _defaultBg = Color.FromArgb(255, 0xfa, 0xfa, 0xfa);
                _defaultFg = Color.FromArgb(255, 0x5c, 0x67, 0x73);
                _ansiColors = AyuLightColors;
                _selectionBg = Color.FromArgb(255, 0xf0, 0xee, 0xe4);
                _cursorColor = Color.FromArgb(255, 0xff, 0xb4, 0x54);
            }
            else if (settings.ColorScheme == "Material Light")
            {
                _defaultBg = Color.FromArgb(255, 0xff, 0xff, 0xff);
                _defaultFg = Color.FromArgb(255, 0x26, 0x32, 0x38);
                _ansiColors = MaterialLightColors;
                _selectionBg = Color.FromArgb(255, 0xee, 0xeb, 0xee);
                _cursorColor = Color.FromArgb(255, 0x26, 0x32, 0x38);
            }
            else if (settings.ColorScheme == "Atom One Light")
            {
                _defaultBg = Color.FromArgb(255, 0xfa, 0xfa, 0xfa);
                _defaultFg = Color.FromArgb(255, 0x38, 0x3a, 0x42);
                _ansiColors = AtomOneLightColors;
                _selectionBg = Color.FromArgb(255, 0xe5, 0xeb, 0xf1);
                _cursorColor = Color.FromArgb(255, 0x38, 0x3a, 0x42);
            }
            else if (settings.ColorScheme == "Solarized Light")
            {
                _defaultBg = Color.FromArgb(255, 0xfd, 0xf6, 0xe3);
                _defaultFg = Color.FromArgb(255, 0x65, 0x7b, 0x83);
                _ansiColors = SolarizedLightColors;
                _selectionBg = Color.FromArgb(255, 0xee, 0xe8, 0xd5);
                _cursorColor = Color.FromArgb(255, 0x58, 0x6e, 0x75);
            }
            else // One Dark / Default
            {
                _defaultBg = Color.FromArgb(255, 12, 12, 12);
                _defaultFg = Color.FromArgb(255, 204, 204, 204);
                _ansiColors = DarkStandardColors;
                _selectionBg = Color.FromArgb(255, 38, 79, 120);
                _cursorColor = Color.FromArgb(255, 204, 204, 204);
            }
            // Apply the theme's background color as the solid canvas background
            // so the terminal is NOT affected by app-level dark/light mode switching.
            // The terminal appearance is controlled exclusively by the sidebar theme picker.
            if (Canvas != null && Canvas.ReadyToDraw)
            {
                Canvas.ClearColor = _defaultBg;
                RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(_defaultBg.A, _defaultBg.R, _defaultBg.G, _defaultBg.B));
                UpdateFont();
            }
            
            if (_session != null && _session.Buffer != null && settings != null)
            {
                _session.Buffer.MaxScrollback = settings.ScrollbackLines;
            }
            
            RequestRedraw();
        }

        private void RequestRedraw()
        {
            if (!_isLoaded) return;

            Interlocked.Exchange(ref _needsRedraw, 1);
            if (Interlocked.CompareExchange(ref _redrawScheduled, 1, 0) != 0) return;
            int generation = Volatile.Read(ref _redrawGeneration);

            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                do
                {
                    await Task.Delay(16); // ~1 frame at 60hz
                    if (generation != Volatile.Read(ref _redrawGeneration)) return;
                    if (Interlocked.Exchange(ref _needsRedraw, 0) != 0 &&
                        _isLoaded && Canvas != null && Canvas.ReadyToDraw)
                    {
                        Canvas.Invalidate();
                    }
                }
                while (Volatile.Read(ref _needsRedraw) != 0 && _isLoaded &&
                       generation == Volatile.Read(ref _redrawGeneration));

                if (generation != Volatile.Read(ref _redrawGeneration)) return;
                Interlocked.Exchange(ref _redrawScheduled, 0);
                if (Volatile.Read(ref _needsRedraw) != 0 && _isLoaded)
                    RequestRedraw();
            }))
            {
                Interlocked.Exchange(ref _redrawScheduled, 0);
            }
        }

        public void FocusTerminal()
        {
            if (!_isLoaded) return;
            Focus(FocusState.Programmatic);
            RequestRedraw();
        }

        // ── Win2D Lifecycle & Measuring ───────────────────────────────────────

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            this.Focus(FocusState.Programmatic);
            if (_session != null)
            {
                _session.Buffer.BufferChanged -= RequestRedraw;
                _session.Buffer.BufferChanged += RequestRedraw;
            }
            RequestRedraw();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
            // Invalidate delayed callbacks from the previous load generation so
            // they cannot interfere with the scheduling state after navigation.
            Interlocked.Increment(ref _redrawGeneration);
            Interlocked.Exchange(ref _redrawScheduled, 0);
            Interlocked.Exchange(ref _needsRedraw, 0);
            if (_session != null)
            {
                _session.Buffer.BufferChanged -= RequestRedraw;
            }
            // Do NOT call Canvas.RemoveFromVisualTree() here so it survives NavigationCacheMode
        }

        public void Dispose()
        {
            DisposeRowCache();
            if (Canvas != null)
            {
                Canvas.RemoveFromVisualTree();
                Canvas = null!;
            }
        }

        private void Canvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            UpdateFont();
            // Don't call UpdatePtySize here – sender.Size may be (0,0) before layout.
            // The real connection is deferred to the first SizeChanged with a valid size.
        }

        private void UpdateFont()
        {
            if (Canvas == null || !Canvas.ReadyToDraw) return;

            _textFormat = new CanvasTextFormat
            {
                FontFamily = string.IsNullOrEmpty(_settings?.FontFamily) ? "Consolas" : _settings.FontFamily,
                FontSize = (float)(_settings?.FontSize ?? 16),
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Top
            };
            InvalidateRowCache();

            // Apply solid background color from the selected terminal theme.
            // This isolates the terminal from app-level dark/light mode changes.
            Canvas.ClearColor = _defaultBg;
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(_defaultBg.A, _defaultBg.R, _defaultBg.G, _defaultBg.B));

            // Use a 20-char string to average out any left/right layout padding.
            // This gives the most accurate per-character advance width for col calculation.
            const int measureCount = 20;
            string measureStr = new string('M', measureCount);
            using var longLayout = new CanvasTextLayout(Canvas, measureStr, _textFormat, 0.0f, 0.0f);
            _charWidth = longLayout.LayoutBounds.Width / measureCount;
            if (_charWidth <= 0) _charWidth = 8;

            // Use LineSpacing from the layout (includes ascender + descender + gap).
            // CanvasTextLayout.LineSpacing is the actual rendered line height.
            _charHeight = longLayout.LineSpacing;
            if (_charHeight <= 0) _charHeight = longLayout.LayoutBounds.Height;
            if (_charHeight <= 0) _charHeight = 16;
            _charHeight = Math.Ceiling(_charHeight);

            if (Canvas.ActualWidth > 0 && Canvas.ActualHeight > 0)
            {
                UpdatePtySize(Canvas.ActualWidth, Canvas.ActualHeight);
            }
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width;
            double h = e.NewSize.Height;

            // Ignore trivially small sizes before layout is complete
            if (w < 50 || h < 30 || _charWidth <= 0 || _charHeight <= 0) return;

            UpdatePtySize(w, h);

            // First meaningful size event → now connect with the correct cols
            if (!_hasConnected && _session != null &&
                _session.State == TerminalSession.SessionState.Disconnected)
            {
                _hasConnected = true;
                _ = _session.ConnectAsync();
            }
        }

        private void UpdatePtySize(double width, double height)
        {
            if (_session == null || _charWidth <= 0 || _charHeight <= 0) return;

            int cols = Math.Max(10, (int)(width / _charWidth));
            int rows = Math.Max(3,  (int)(height / _charHeight));

            bool sizeChanged  = cols != _session.Buffer.Cols || rows != _session.Buffer.Rows;

            if (sizeChanged)
            {
                _session.Buffer.Resize(cols, rows);
                _session.PtyBridge.SetSize(cols, rows, _session.Transport);
                RequestRedraw();
            }

        }

        // ── Rendering Loop ────────────────────────────────────────────────────

        private bool IsCellSelected(int x, int y)
        {
            if (_selectionStart == null || _selectionEnd == null) return false;

            var a = _selectionStart.Value;
            var b = _selectionEnd.Value;

            // Normalise so 'start' is always the earlier position
            (int x, int y) start, end;
            if (a.y < b.y || (a.y == b.y && a.x <= b.x))
            { start = a; end = b; }
            else
            { start = b; end = a; }

            // Single-cell selection = nothing highlighted (pure click, no drag)
            if (start.x == end.x && start.y == end.y) return false;

            if (start.y == end.y)
                return y == start.y && x >= start.x && x <= end.x;

            if (y == start.y) return x >= start.x;
            if (y == end.y)   return x <= end.x;
            return y > start.y && y < end.y;
        }

        private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_session == null || _textFormat == null) return;

            var buffer = _session.Buffer;
            int rows;
            int cols;
            int cursorX;
            int cursorY;
            TerminalCell cursorCell = default;
            TerminalRow?[] sources;
            long[] versions;

            lock (buffer.SyncRoot)
            {
                rows = buffer.Rows;
                cols = buffer.Cols;
                cursorX = buffer.CursorX;
                cursorY = buffer.CursorY + buffer.Rows - Math.Min(rows, buffer.Lines.Count) + _scrollOffset;
                if (_visibleRows.Length != rows)
                {
                    _visibleRows = new TerminalRow?[rows];
                    _visibleVersions = new long[rows];
                }
                sources = _visibleRows;
                versions = _visibleVersions;

                int totalLines = buffer.Scrollback.Count + buffer.Lines.Count;
                int startLineIndex = Math.Max(0, totalLines - rows - _scrollOffset);
                for (int y = 0; y < rows; y++)
                {
                    int absoluteY = startLineIndex + y;
                    TerminalRow? row = absoluteY < buffer.Scrollback.Count
                        ? buffer.Scrollback[absoluteY]
                        : absoluteY - buffer.Scrollback.Count < buffer.Lines.Count
                            ? buffer.Lines[absoluteY - buffer.Scrollback.Count]
                            : null;
                    sources[y] = row;
                    versions[y] = row?.Version ?? -1;
                }

                if ((uint)cursorY < (uint)rows && (uint)cursorX < (uint)cols)
                {
                    var cursorRow = sources[cursorY];
                    if (cursorRow != null && cursorX < cursorRow.Cells.Length)
                        cursorCell = cursorRow.Cells[cursorX];
                }
            }

            EnsureRowCache(rows);
            int selectionKey = HashCode.Combine(_selectionStart, _selectionEnd);
            for (int y = 0; y < rows; y++)
            {
                RowRenderCache cache = _rowCache[y];
                TerminalRow? source = sources[y];
                bool isLiveCursorRow = _scrollOffset == 0 && y == cursorY;
                if (isLiveCursorRow || !ReferenceEquals(cache.Source, source) || cache.Version != versions[y] ||
                    cache.Columns != cols || cache.SelectionKey != selectionKey ||
                    cache.Generation != _rowRenderGeneration || cache.Commands == null)
                {
                    TerminalCell[] cells = ArrayPool<TerminalCell>.Shared.Rent(cols);
                    try
                    {
                        var empty = new TerminalCell { Char = ' ', FgColor = TerminalCell.DefaultFg, BgColor = TerminalCell.DefaultBg };
                        Array.Fill(cells, empty, 0, cols);
                        if (source != null)
                        {
                            lock (buffer.SyncRoot)
                            {
                                Array.Copy(source.Cells, 0, cells, 0, Math.Min(cols, source.Cells.Length));
                                versions[y] = source.Version;
                            }
                        }

                        var commands = new CanvasCommandList(sender);
                        using (CanvasDrawingSession rowDrawing = commands.CreateDrawingSession())
                            DrawCachedRow(rowDrawing, cells, cols, y);

                        cache.Commands?.Dispose();
                        cache.Commands = commands;
                        cache.Source = source;
                        cache.Version = versions[y];
                        cache.Columns = cols;
                        cache.SelectionKey = selectionKey;
                        cache.Generation = _rowRenderGeneration;
                    }
                    finally
                    {
                        ArrayPool<TerminalCell>.Shared.Return(cells);
                    }
                }

                if (cache.Commands != null)
                    args.DrawingSession.DrawImage(cache.Commands, 0, (float)(y * _charHeight));
            }

            if (_scrollOffset == 0 && (uint)cursorY < (uint)rows && (uint)cursorX < (uint)cols &&
                FocusState != FocusState.Unfocused)
            {
                float cursorPixelX = GetCursorVisualX(sender, sources[cursorY], cursorX);
                DrawCursor(args.DrawingSession, cursorCell, cursorPixelX, cursorY);
            }
        }

        private float GetCursorVisualX(CanvasControl canvas, TerminalRow? row, int cursorX)
        {
            if (row == null || cursorX <= 0 || _textFormat == null)
                return (float)(cursorX * _charWidth);

            TerminalCell[] cells;
            lock (_session?.Buffer.SyncRoot ?? row)
            {
                int count = Math.Min(cursorX, row.Cells.Length);
                cells = ArrayPool<TerminalCell>.Shared.Rent(count);
                Array.Copy(row.Cells, cells, count);
                cursorX = count;
            }

            try
            {
                var textChunk = new StringBuilder(cursorX);
                int startX = 0;
                int logicalWidth = 0;
                TerminalCell currentAttr = cells[0];

                for (int x = 0; x < cursorX; x++)
                {
                    TerminalCell cell = cells[x];
                    if (cell.FgColor != currentAttr.FgColor || cell.BgColor != currentAttr.BgColor ||
                        cell.IsBold != currentAttr.IsBold || cell.IsItalic != currentAttr.IsItalic ||
                        cell.IsUnderline != currentAttr.IsUnderline)
                    {
                        textChunk.Clear();
                        startX = x;
                        logicalWidth = 0;
                        currentAttr = cell;
                    }

                    if (cell.Char != '\0')
                        textChunk.Append(cell.Char == 0 ? ' ' : cell.Char);
                    logicalWidth++;
                }

                return (float)(startX * _charWidth +
                    MeasureTextAdvance(canvas, textChunk.ToString(), logicalWidth));
            }
            finally
            {
                ArrayPool<TerminalCell>.Shared.Return(cells);
            }
        }

        private double MeasureTextAdvance(CanvasControl canvas, string text, int logicalWidth)
        {
            if (_textFormat == null || logicalWidth <= 0)
                return 0;

            if (string.IsNullOrEmpty(text))
                return logicalWidth * _charWidth;

            if (string.IsNullOrWhiteSpace(text))
                return Math.Max(logicalWidth, text.Length) * _charWidth;

            const string sentinel = "M";
            using var textLayout = new CanvasTextLayout(canvas, text + sentinel, _textFormat, 0.0f, 0.0f);
            using var sentinelLayout = new CanvasTextLayout(canvas, sentinel, _textFormat, 0.0f, 0.0f);
            double measured = textLayout.LayoutBounds.Width - sentinelLayout.LayoutBounds.Width;

            if (double.IsNaN(measured) || measured <= 0)
                return logicalWidth * _charWidth;

            return measured;
        }

        private void DrawCachedRow(CanvasDrawingSession ds, TerminalCell[] cells, int cols, int selectionY)
        {
            var textChunk = new StringBuilder(cols);
            int startX = 0;
            TerminalCell currentAttr = cells[0];
            int logicalWidth = 0;

            for (int x = 0; x < cols; x++)
            {
                TerminalCell cell = cells[x];
                if (IsCellSelected(x, selectionY + _scrollOffset))
                    cell.BgColor = TerminalCell.SelectionBgMask;

                if (cell.FgColor != currentAttr.FgColor || cell.BgColor != currentAttr.BgColor ||
                    cell.IsBold != currentAttr.IsBold || cell.IsItalic != currentAttr.IsItalic ||
                    cell.IsUnderline != currentAttr.IsUnderline)
                {
                    DrawChunk(ds, textChunk.ToString(), startX, 0, currentAttr, logicalWidth);
                    textChunk.Clear();
                    startX = x;
                    logicalWidth = 0;
                    currentAttr = cell;
                }

                if (cell.Char != '\0') textChunk.Append(cell.Char == 0 ? ' ' : cell.Char);
                logicalWidth++;
            }

            if (logicalWidth > 0)
                DrawChunk(ds, textChunk.ToString(), startX, 0, currentAttr, logicalWidth);
        }

        private void DrawCursor(CanvasDrawingSession ds, TerminalCell cell, float xPos, int y)
        {
            string text = cell.Char is '\0' or (char)0 ? " " : cell.Char.ToString();
            float yPos = (float)(y * _charHeight);
            if (_settings.CursorStyle == "Underline")
            {
                DrawChunkAt(ds, text, xPos, yPos, cell, 1);
                ds.DrawLine(xPos, (float)((y + 1) * _charHeight - 1),
                    (float)(xPos + _charWidth), (float)((y + 1) * _charHeight - 1), _cursorColor, 2);
            }
            else if (_settings.CursorStyle == "Bar")
            {
                DrawChunkAt(ds, text, xPos, yPos, cell, 1);
                ds.DrawLine(xPos + 1, yPos,
                    xPos + 1, (float)((y + 1) * _charHeight), _cursorColor, 2);
            }
            else
            {
                ds.FillRectangle(xPos, yPos,
                    (float)_charWidth, (float)_charHeight, _cursorColor);
                Color inverted = Color.FromArgb(_cursorColor.A, _defaultBg.R, _defaultBg.G, _defaultBg.B);
                ds.DrawText(text, xPos, yPos, inverted, _textFormat);
            }
        }

        private void EnsureRowCache(int rows)
        {
            if (_rowCache.Length == rows) return;
            foreach (RowRenderCache cache in _rowCache) cache?.Dispose();
            _rowCache = new RowRenderCache[rows];
            for (int i = 0; i < rows; i++) _rowCache[i] = new RowRenderCache();
        }

        private void InvalidateRowCache() => _rowRenderGeneration++;

        private void DisposeRowCache()
        {
            foreach (RowRenderCache cache in _rowCache) cache?.Dispose();
            _rowCache = Array.Empty<RowRenderCache>();
            _visibleRows = Array.Empty<TerminalRow?>();
            _visibleVersions = Array.Empty<long>();
        }

        private void Canvas_DrawLegacy(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_session == null || _textFormat == null) return;

            var ds = args.DrawingSession;
            var buffer = _session.Buffer;
            int rows;
            int cols;
            int cursorX;
            int cursorY;
            TerminalCell[] cells;

            lock (buffer.SyncRoot)
            {
                rows = buffer.Rows;
                cols = buffer.Cols;
                cursorX = buffer.CursorX;
                cursorY = buffer.CursorY + buffer.Rows - Math.Min(rows, buffer.Lines.Count) + _scrollOffset;

                int cellCount = rows * cols;
                cells = ArrayPool<TerminalCell>.Shared.Rent(cellCount);
                var emptyCell = new TerminalCell
                {
                    Char = ' ',
                    FgColor = TerminalCell.DefaultFg,
                    BgColor = TerminalCell.DefaultBg
                };
                Array.Fill(cells, emptyCell, 0, cellCount);

                int totalLines = buffer.Scrollback.Count + buffer.Lines.Count;
                int startLineIndex = Math.Max(0, totalLines - rows - _scrollOffset);
                for (int y = 0; y < rows; y++)
                {
                    int absY = startLineIndex + y;
                    TerminalRow? row = null;
                    if (absY < buffer.Scrollback.Count)
                        row = buffer.Scrollback[absY];
                    else if (absY - buffer.Scrollback.Count < buffer.Lines.Count)
                        row = buffer.Lines[absY - buffer.Scrollback.Count];

                    if (row?.Cells != null)
                        Array.Copy(row.Cells, 0, cells, y * cols, Math.Min(cols, row.Cells.Length));
                }
            }

            try
            {
                StringBuilder textChunk = new StringBuilder(cols);
                for (int y = 0; y < rows; y++)
                {
                    int rowOffset = y * cols;
                    int startX = 0;
                    TerminalCell currentAttr = cells[rowOffset];
                    textChunk.Clear();
                    int chunkLogicalWidth = 0;

                    for (int x = 0; x < cols; x++)
                    {
                        var cell = cells[rowOffset + x];

                        if (IsCellSelected(x, y + _scrollOffset))
                        {
                            cell.BgColor = TerminalCell.SelectionBgMask;
                        }

                        // Draw cursor background block (only if we are not scrolled up)
                        if (x == cursorX && y == cursorY && _scrollOffset == 0 && this.FocusState != FocusState.Unfocused)
                        {
                            if (textChunk.Length > 0 || chunkLogicalWidth > 0)
                            {
                                DrawChunk(ds, textChunk.ToString(), startX, y, currentAttr, chunkLogicalWidth);
                                textChunk.Clear();
                                chunkLogicalWidth = 0;
                            }
                            
                            var invertedAttr = cell;
                            invertedAttr.FgColor = (uint)((_defaultBg.A << 24) | (_defaultBg.R << 16) | (_defaultBg.G << 8) | _defaultBg.B);
                            
                            string cursorText = cell.Char == '\0' ? " " : cell.Char.ToString();

                            if (_settings.CursorStyle == "Underline")
                            {
                                DrawChunk(ds, cursorText, x, y, cell, 1);
                                ds.DrawLine((float)(x * _charWidth), (float)((y + 1) * _charHeight - 1),
                                            (float)((x + 1) * _charWidth), (float)((y + 1) * _charHeight - 1), _cursorColor, 2);
                            }
                            else if (_settings.CursorStyle == "Bar")
                            {
                                DrawChunk(ds, cursorText, x, y, cell, 1);
                                ds.DrawLine((float)(x * _charWidth + 1), (float)(y * _charHeight),
                                            (float)(x * _charWidth + 1), (float)((y + 1) * _charHeight), _cursorColor, 2);
                            }
                            else
                            {
                                // Block cursor: fill background, then draw inverted text on top
                                ds.FillRectangle((float)(x * _charWidth), (float)(y * _charHeight),
                                                 (float)_charWidth, (float)_charHeight, _cursorColor);
                                // Draw the character in background (inverted) color on top of cursor block
                                Color invertedText = Color.FromArgb(_cursorColor.A, _defaultBg.R, _defaultBg.G, _defaultBg.B);
                                ds.DrawText(cursorText, (float)(x * _charWidth), (float)(y * _charHeight), invertedText, _textFormat);
                            }
                            
                            startX = x + 1;
                            if (x + 1 < cols) currentAttr = cells[rowOffset + x + 1];
                            continue;
                        }

                        if (cell.FgColor != currentAttr.FgColor || 
                            cell.BgColor != currentAttr.BgColor ||
                            cell.IsBold != currentAttr.IsBold)
                        {
                            if (textChunk.Length > 0 || chunkLogicalWidth > 0)
                            {
                                DrawChunk(ds, textChunk.ToString(), startX, y, currentAttr, chunkLogicalWidth);
                                textChunk.Clear();
                                chunkLogicalWidth = 0;
                            }
                            startX = x;
                            currentAttr = cell;
                        }

                        if (cell.Char != '\0')
                        {
                            textChunk.Append(cell.Char == 0 ? ' ' : cell.Char);
                        }
                        chunkLogicalWidth++;
                    }

                    if (textChunk.Length > 0 || chunkLogicalWidth > 0)
                    {
                        DrawChunk(ds, textChunk.ToString(), startX, y, currentAttr, chunkLogicalWidth);
                    }
                }
            }
            finally
            {
                ArrayPool<TerminalCell>.Shared.Return(cells);
            }
        }

        private void DrawChunk(CanvasDrawingSession ds, string text, int startX, int y, TerminalCell attr, int logicalWidth)
        {
            float xPos = (float)(startX * _charWidth);
            float yPos = (float)(y * _charHeight);
            DrawChunkAt(ds, text, xPos, yPos, attr, logicalWidth);
        }

        private void DrawChunkAt(CanvasDrawingSession ds, string text, float xPos, float yPos, TerminalCell attr, int logicalWidth)
        {
            // Always fill background — use _defaultBg for cells with no explicit background.
            // This ensures no transparent gaps and isolates the terminal from app theme changes.
            Color bg = ParseColor(attr.BgColor, _defaultBg);
            ds.FillRectangle(xPos, yPos, (float)(logicalWidth * _charWidth), (float)_charHeight, bg);

            if (string.IsNullOrWhiteSpace(text)) return;

            Color fg = EnsureReadableTextColor(ParseColor(attr.FgColor, _defaultFg), bg);
            
            // Temp bold implementation: use standard text format but draw twice slightly offset
            // Real bold needs font weight changes, but caching formats is complex for Phase 4
            ds.DrawText(text, xPos, yPos, fg, _textFormat);
            if (attr.IsBold)
            {
                ds.DrawText(text, xPos + 0.5f, yPos, fg, _textFormat);
            }
        }

        private Color ParseColor(uint c, Color fallback)
        {
            if (c == TerminalCell.SelectionBgMask) return _selectionBg;
            if (c == TerminalCell.DefaultFg || c == TerminalCell.DefaultBg) return fallback;
            if ((c & 0xFF000000) == TerminalCell.IndexedColorMask)
            {
                int idx = (int)(c & 0xFF);
                if (idx >= 0 && idx < 16) return _ansiColors[idx];
                return ParseXterm256Color(idx);
            }
            return Color.FromArgb(255, (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
        }

        private static Color ParseXterm256Color(int index)
        {
            // 16-231: 6x6x6 RGB cube. 232-255: 24-step grayscale ramp.
            if (index is >= 16 and <= 231)
            {
                int value = index - 16;
                int red = value / 36;
                int green = value / 6 % 6;
                int blue = value % 6;
                return Color.FromArgb(255, XtermCubeChannel(red), XtermCubeChannel(green), XtermCubeChannel(blue));
            }
            if (index is >= 232 and <= 255)
            {
                byte gray = (byte)(8 + (index - 232) * 10);
                return Color.FromArgb(255, gray, gray, gray);
            }
            return Color.FromArgb(255, 136, 136, 136);
        }

        private static byte XtermCubeChannel(int component) =>
            component == 0 ? (byte)0 : (byte)(55 + component * 40);

        /// <summary>
        /// Keeps ANSI colors recognizable while preventing dark blue/grey text from
        /// disappearing into dark themes (and pale colors into light themes).
        /// </summary>
        private static Color EnsureReadableTextColor(Color foreground, Color background)
        {
            const double minimumContrast = 4.5;
            if (ContrastRatio(foreground, background) >= minimumContrast)
                return foreground;

            bool lighten = RelativeLuminance(background) < 0.5;
            Color adjusted = foreground;
            for (int amount = 12; amount <= 192; amount += 12)
            {
                adjusted = Color.FromArgb(foreground.A,
                    BlendChannel(foreground.R, lighten ? (byte)255 : (byte)0, amount),
                    BlendChannel(foreground.G, lighten ? (byte)255 : (byte)0, amount),
                    BlendChannel(foreground.B, lighten ? (byte)255 : (byte)0, amount));
                if (ContrastRatio(adjusted, background) >= minimumContrast)
                    return adjusted;
            }
            return adjusted;
        }

        private static byte BlendChannel(byte source, byte target, int amount) =>
            (byte)(source + (target - source) * amount / 255);

        private static double ContrastRatio(Color first, Color second)
        {
            double lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
            double darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            static double Linear(byte channel)
            {
                double value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }

        // ── Input Handling ────────────────────────────────────────────────────

        private void UserControl_GettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            RequestRedraw(); // Show cursor
        }

        private void UserControl_LosingFocus(UIElement sender, LosingFocusEventArgs args)
        {
            RequestRedraw(); // Hide cursor
        }


        private void UserControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            this.Focus(FocusState.Pointer);
            e.Handled = true;

            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed)
            {
                // Right click: paste clipboard into SSH stream
                _ = PasteFromClipboard();
                // Clear any active selection
                _isSelecting   = false;
                _selectionStart = null;
                _selectionEnd   = null;
                RequestRedraw();
            }
            else if (point.Properties.IsLeftButtonPressed)
            {
                // Left click: clear old selection, begin new one
                _isSelecting   = true;
                this.CapturePointer(e.Pointer);
                int x = Math.Max(0, (int)(point.Position.X / _charWidth));
                int y = Math.Max(0, (int)(point.Position.Y / _charHeight)) + _scrollOffset;
                _selectionStart = (x, y);
                _selectionEnd   = (x, y); // same = no visible highlight until drag
                // Don't call RequestRedraw here – avoids the flicker on plain click.
            }
        }

        private void UserControl_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isSelecting && _selectionStart != null)
            {
                var point = e.GetCurrentPoint(this);
                int x = Math.Max(0, (int)(point.Position.X / _charWidth));
                int y = Math.Max(0, (int)(point.Position.Y / _charHeight)) + _scrollOffset;
                _selectionEnd = (x, y);
                RequestRedraw();
            }
        }

        private void UserControl_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            this.ReleasePointerCapture(e.Pointer);
            if (_isSelecting)
            {
                _isSelecting = false;
                if (_selectionStart != null && _selectionEnd != null && _selectionStart != _selectionEnd)
                {
                    // Copy text to clipboard
                    CopyToClipboard();
                }
                else
                {
                    _selectionStart = null;
                    _selectionEnd = null;
                    RequestRedraw();
                }
            }
        }

        private void UserControl_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_session == null || _session.Buffer == null) return;

            var point = e.GetCurrentPoint(this);
            int delta = point.Properties.MouseWheelDelta;
            int scrollLines = delta / 40; // 120 per notch is typical -> 3 lines per notch

            int newOffset = _scrollOffset + scrollLines;
            if (newOffset < 0) newOffset = 0;
            if (newOffset > _session.Buffer.Scrollback.Count) newOffset = _session.Buffer.Scrollback.Count;

            if (newOffset != _scrollOffset)
            {
                _scrollOffset = newOffset;
                RequestRedraw();
            }
        }

        private async System.Threading.Tasks.Task PasteFromClipboard()
        {
            if (_session == null || !_session.Transport.IsConnected) return;

            var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackageView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                string text = await dataPackageView.GetTextAsync();
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                _session.Transport.SendRaw(bytes);
            }
        }

        private void CopyToClipboard()
        {
            if (_selectionStart == null || _selectionEnd == null || _session == null) return;

            var start = _selectionStart.Value;
            var end = _selectionEnd.Value;

            if (start.y > end.y || (start.y == end.y && start.x > end.x))
            {
                var temp = start;
                start = end;
                end = temp;
            }

            string text = _session.Buffer.GetText(start.x, start.y, end.x, end.y);
            if (!string.IsNullOrEmpty(text))
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            }
        }

        // WinUI 3 keyboard routing: KeyDown for control keys, CharacterReceived for text
        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (_session == null || !_session.Transport.IsConnected) return;

            byte[]? seq = null;
            bool handled = true;

            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            switch (e.Key)
            {
                case Windows.System.VirtualKey.Up:    seq = new byte[] { 27, (byte)'[', (byte)'A' }; break;
                case Windows.System.VirtualKey.Down:  seq = new byte[] { 27, (byte)'[', (byte)'B' }; break;
                case Windows.System.VirtualKey.Right: seq = new byte[] { 27, (byte)'[', (byte)'C' }; break;
                case Windows.System.VirtualKey.Left:  seq = new byte[] { 27, (byte)'[', (byte)'D' }; break;
                
                case Windows.System.VirtualKey.Insert: seq = new byte[] { 27, (byte)'[', (byte)'2', (byte)'~' }; break;
                case Windows.System.VirtualKey.Delete: seq = new byte[] { 27, (byte)'[', (byte)'3', (byte)'~' }; break;
                case Windows.System.VirtualKey.Home:   seq = new byte[] { 27, (byte)'[', (byte)'H' }; break;
                case Windows.System.VirtualKey.End:    seq = new byte[] { 27, (byte)'[', (byte)'F' }; break;
                case Windows.System.VirtualKey.PageUp: seq = new byte[] { 27, (byte)'[', (byte)'5', (byte)'~' }; break;
                case Windows.System.VirtualKey.PageDown:seq= new byte[] { 27, (byte)'[', (byte)'6', (byte)'~' }; break;
                
                case Windows.System.VirtualKey.Tab:
                    seq = new byte[] { 9 };
                    break;
                case Windows.System.VirtualKey.Enter:
                    seq = new byte[] { 13 }; // CR
                    break;
                case Windows.System.VirtualKey.Back:
                    seq = new byte[] { 127 }; // DEL (Backspace in most modern Linux)
                    break;
                case Windows.System.VirtualKey.Escape:
                    seq = new byte[] { 27 };
                    break;

                case Windows.System.VirtualKey.C:
                    if (ctrl) seq = new byte[] { 3 }; // Ctrl+C
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.V:
                    if (ctrl)
                    {
                        _ = PasteFromClipboard();
                    }
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.D:
                    if (ctrl) seq = new byte[] { 4 }; // Ctrl+D
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.L:
                    if (ctrl) seq = new byte[] { 12 }; // Ctrl+L (Clear screen)
                    else handled = false;
                    break;
                case Windows.System.VirtualKey.Z:
                    if (ctrl) seq = new byte[] { 26 }; // Ctrl+Z
                    else handled = false;
                    break;

                default:
                    handled = false;
                    break;
            }

            if (seq != null)
            {
                _session.Transport.SendRaw(seq);
                e.Handled = true;
            }
            else if (handled)
            {
                e.Handled = true;
            }
            
            base.OnKeyDown(e);
        }

        // Add CharacterReceived via constructor or event binding since UserControl doesn't have a virtual method for it
        private void UIElement_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
        {
            if (_session == null || !_session.Transport.IsConnected) return;

            // Control characters are already handled by OnKeyDown. This prevents
            // Enter and Tab from being sent twice on WinUI event paths that raise both.
            if (args.Character < 32 || args.Character == 127)
            {
                args.Handled = true;
                return;
            }

            char character = args.Character;
            if (char.IsHighSurrogate(character))
            {
                _pendingHighSurrogate = character;
                args.Handled = true;
                return;
            }

            string text;
            if (char.IsLowSurrogate(character) && _pendingHighSurrogate.HasValue)
            {
                text = new string(new[] { _pendingHighSurrogate.Value, character });
                _pendingHighSurrogate = null;
            }
            else
            {
                if (_pendingHighSurrogate.HasValue)
                {
                    _session.Transport.SendInput(_pendingHighSurrogate.Value.ToString());
                    _pendingHighSurrogate = null;
                }
                text = character.ToString();
            }

            _session.Transport.SendInput(text);
            args.Handled = true;
        }

        // Removed OnApplyTemplate since we register in constructor now
    }
}
