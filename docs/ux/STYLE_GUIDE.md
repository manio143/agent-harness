# ACP Client (Avalonia) — Style Guide & Implementation Guidelines

This document provides actionable design tokens, component patterns, and interaction rules for the Avalonia ACP client.

**Design goals:** Dark theme, developer-tool density, progressive disclosure, pretty but practical.

---

## 1. Design Principles

### 1.1 Visual Hierarchy

| Level | Purpose | Treatment |
|-------|---------|-----------|
| **Primary** | User messages, agent responses | Full contrast (`#E6E6E6`), standard size (14px) |
| **Secondary** | Intent group headers, labels | Bold text, medium contrast |
| **Tertiary** | Tool previews, metadata | Muted (`#6B7280`), smaller size (11–12px) |
| **Interactive** | Buttons, clickable rows | Hover states, pointer cursor |

### 1.2 Density

> "Dense like VS Code's debug panel, not cramped like a spreadsheet."

- **Vertical rhythm:** 4px base unit. Use multiples: 4, 8, 12, 16, 20, 24.
- **Component spacing:** 8px between sibling items in lists.
- **Section spacing:** 12px between logical sections.
- **Padding:** 6–10px internal, 8–12px external.

### 1.3 Progressive Disclosure

1. **Default:** Show summary (intent title + tool count + status dots)
2. **Hover/Click:** Reveal previews (truncated input/output)
3. **Drill-in:** Full detail via Flyout (scrollable JSON, copy buttons)

**Rule:** Never force scrolling at the summary level. Truncate with ellipsis.

---

## 2. Color Tokens

### 2.1 Background Palette

| Token | Hex | Usage |
|-------|-----|-------|
| `BgBase` | `#0B0D10` | App chrome, flyouts, composer |
| `BgPrimary` | `#111318` | Main content area (ChatView) |
| `BgSecondary` | `#1A1D24` | Message bubbles, cards, text inputs |
| `BgTertiary` | `#151820` | Nested containers (tool rows) |
| `BgBadge` | `#2A3142` | Count badges, chips |

### 2.2 Border Palette

| Token | Hex | Usage |
|-------|-----|-------|
| `BorderSubtle` | `#2A3040` | Card borders, input borders |
| `BorderError` | `#5C2020` | Error state borders |

### 2.3 Text Palette

| Token | Hex | Usage |
|-------|-----|-------|
| `TextPrimary` | `#E6E6E6` | Main text, titles |
| `TextSecondary` | `#B9C0CC` | Labels, descriptions |
| `TextMuted` | `#6B7280` | Metadata, placeholders, timestamps |
| `TextSuccess` | `#6B9A7D` | Output previews (inline) |
| `TextSuccessBright` | `#8FBFA3` | Output JSON (detail view) |
| `TextError` | `#FFB4B4` | Error messages |

### 2.4 Status Colors

| State | Hex | Usage |
|-------|-----|-------|
| `StatusRunning` | `#FBBF24` | Yellow — in progress |
| `StatusSuccess` | `#22C55E` | Green — completed |
| `StatusError` | `#EF4444` | Red — failed |
| `StatusPending` | `#6B7280` | Gray — queued |

### 2.5 Accent Colors

| Token | Hex | Usage |
|-------|-----|-------|
| `AccentPrimary` | `#3B82F6` | Primary buttons (Connect, Send) |
| `AccentHover` | `#2563EB` | Button hover state |
| `BgErrorBanner` | `#2A1515` | Error banner background |

### 2.6 Applying Colors in Avalonia

Define as `StaticResource` in `App.axaml` for consistency:

```xml
<Application.Resources>
    <SolidColorBrush x:Key="BgBase" Color="#0B0D10" />
    <SolidColorBrush x:Key="BgPrimary" Color="#111318" />
    <SolidColorBrush x:Key="BgSecondary" Color="#1A1D24" />
    <SolidColorBrush x:Key="BorderSubtle" Color="#2A3040" />
    <SolidColorBrush x:Key="TextPrimary" Color="#E6E6E6" />
    <SolidColorBrush x:Key="TextMuted" Color="#6B7280" />
    <SolidColorBrush x:Key="AccentPrimary" Color="#3B82F6" />
    <!-- ... -->
</Application.Resources>
```

Usage:
```xml
<Border Background="{StaticResource BgSecondary}" 
        BorderBrush="{StaticResource BorderSubtle}" />
```

---

## 3. Typography

### 3.1 Font Stack

| Purpose | Family | Fallback |
|---------|--------|----------|
| UI text | System default (FluentTheme) | Segoe UI, San Francisco |
| Code/JSON | `Consolas` | `Courier New`, `monospace` |

### 3.2 Size Scale

| Token | Size | Usage |
|-------|------|-------|
| `TextXs` | 11px | Badges, tiny labels |
| `TextSm` | 12px | Metadata, error text, code previews |
| `TextBase` | 14px | Body text, messages (default) |
| `TextLg` | 16px | Section headers |
| `TextXl` | 18px | Screen titles |

### 3.3 Weight

| Weight | Value | Usage |
|--------|-------|-------|
| Normal | `FontWeight.Normal` | Body text |
| Medium | `FontWeight.Medium` | Tool names, emphasis |
| SemiBold | `FontWeight.SemiBold` | Screen titles |
| Bold | `FontWeight.Bold` | Intent group headers |

### 3.4 XAML Patterns

```xml
<!-- Screen title -->
<TextBlock Text="Connect to ACP agent" 
           FontSize="18" FontWeight="SemiBold" 
           Foreground="#E6E6E6" />

<!-- Intent group header -->
<TextBlock Text="{Binding Title}" 
           FontSize="14" FontWeight="Bold" 
           Foreground="#E6E6E6" />

<!-- Code preview -->
<TextBlock Text="{Binding InputPreview}" 
           FontFamily="Consolas" FontSize="11" 
           Foreground="#6B7280" 
           MaxLines="1" TextTrimming="CharacterEllipsis" />
```

---

## 4. Spacing Tokens

### 4.1 Padding

| Token | Value | Usage |
|-------|-------|-------|
| `PadXs` | 4px | Compact badges |
| `PadSm` | 6px | Tool row internal |
| `PadMd` | 8px | Card internal, intent group content |
| `PadLg` | 10–12px | Main containers, composer |
| `PadXl` | 20px | Screen-level (ConnectionView) |

### 4.2 Margins

| Token | Value | Usage |
|-------|-------|-------|
| `SpaceXs` | 2px | Inline element gaps |
| `SpaceSm` | 4px | Label-to-input, tight groups |
| `SpaceMd` | 6–8px | Between list items, button groups |
| `SpaceLg` | 12px | Section separations |

### 4.3 Corner Radius

| Token | Value | Usage |
|-------|-------|-------|
| `RadiusSm` | 4px | Badges, tool rows, inputs |
| `RadiusMd` | 6px | Message bubbles, cards |
| `RadiusLg` | 8px | Flyouts, modals |

---

## 5. Component Guidelines

### 5.1 Message Bubble (User/Agent Text)

```xml
<Border Background="#1A1D24" 
        CornerRadius="6" 
        Padding="10"
        BorderBrush="#2A3040" 
        BorderThickness="1">
    <TextBlock Text="{Binding}" 
               Foreground="#E6E6E6" 
               TextWrapping="Wrap" />
</Border>
```

**Rules:**
- Same style for user and agent messages (no left/right alignment—this is a tool, not a chat app)
- Wrap text, never truncate prose
- Use `Margin="0,0,0,8"` via `ItemsControl.Styles` for list spacing

### 5.2 Intent Group Header

```xml
<Expander IsExpanded="{Binding IsExpanded}">
    <Expander.Header>
        <StackPanel Orientation="Horizontal" Spacing="8">
            <TextBlock Text="{Binding Title}" 
                       FontSize="14" FontWeight="Bold" 
                       Foreground="#E6E6E6" />
            <Border Background="#2A3142" CornerRadius="4" Padding="6,2">
                <TextBlock Text="{Binding Items.Count}" 
                           FontSize="11" Foreground="#8B95A5" />
            </Border>
        </StackPanel>
    </Expander.Header>
    <!-- Content: tool rows -->
</Expander>
```

**Rules:**
- Default expanded for active/running groups
- Auto-collapse completed groups (future: settings toggle)
- Badge count always visible (don't hide at 0)

### 5.3 Tool Row (Inside Intent Group)

```xml
<Button Background="Transparent" Padding="0" 
        HorizontalContentAlignment="Stretch">
    <Button.Flyout>
        <Flyout Placement="Right">
            <views:ToolCallDetailView DataContext="{Binding Detail}" />
        </Flyout>
    </Button.Flyout>

    <Border Background="#151820" CornerRadius="4" Padding="6" 
            Margin="0,0,0,4"
            BorderBrush="#2A3040" BorderThickness="1">
        <StackPanel Spacing="2">
            <StackPanel Orientation="Horizontal" Spacing="6">
                <Ellipse Width="8" Height="8" 
                         Fill="{Binding StatusColor}" 
                         VerticalAlignment="Center" />
                <TextBlock Text="{Binding Title}" 
                           Foreground="#E6E6E6" FontWeight="Medium" />
            </StackPanel>
            <TextBlock Text="{Binding InputPreview}" 
                       Foreground="#6B7280" FontFamily="Consolas" FontSize="11"
                       MaxLines="1" TextTrimming="CharacterEllipsis" />
            <TextBlock Text="{Binding OutputPreview}" 
                       Foreground="#6B9A7D" FontFamily="Consolas" FontSize="11"
                       MaxLines="1" TextTrimming="CharacterEllipsis" />
        </StackPanel>
    </Border>
</Button>
```

**Rules:**
- Status dot (8×8 Ellipse), never text status
- Input preview: gray, single line, ellipsis
- Output preview: green-tinted, single line, ellipsis
- Click → Flyout (not inline expansion)

### 5.4 Tool Detail View (Flyout)

```xml
<Border Background="#0B0D10" Padding="12" CornerRadius="8" 
        MinWidth="400" MaxWidth="700"
        BorderBrush="#2A3040" BorderThickness="1">
    <StackPanel Spacing="8">
        <!-- Header -->
        <StackPanel Orientation="Horizontal" Spacing="8">
            <TextBlock Text="{Binding Title}" FontSize="14" 
                       FontWeight="SemiBold" Foreground="#E6E6E6" />
            <TextBlock Text="{Binding Status}" Foreground="#6B7280" 
                       FontSize="12" VerticalAlignment="Center" />
        </StackPanel>

        <!-- Input section -->
        <StackPanel Spacing="4">
            <TextBlock Text="Input" Foreground="#6B7280" FontSize="11" />
            <Border Background="#151820" CornerRadius="4" Padding="6" 
                    BorderBrush="#2A3040" BorderThickness="1">
                <ScrollViewer MaxHeight="160" 
                              VerticalScrollBarVisibility="Auto">
                    <TextBlock Text="{Binding RawInputJson}" 
                               Foreground="#B9C0CC" FontFamily="Consolas" 
                               FontSize="12" TextWrapping="Wrap" />
                </ScrollViewer>
            </Border>
        </StackPanel>

        <!-- Output section (same pattern) -->

        <!-- Actions -->
        <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Content="Copy input" Command="{Binding CopyInputCommand}" 
                    FontSize="12" Padding="8,4" />
            <Button Content="Copy output" Command="{Binding CopyOutputCommand}" 
                    FontSize="12" Padding="8,4" />
        </StackPanel>
    </StackPanel>
</Border>
```

**Rules:**
- `MaxHeight="160"` on ScrollViewer prevents flyout explosion
- Always show both input and output sections (even if empty—show "null" or "{}")
- Copy buttons: compact (8,4 padding), no icons (text only)

### 5.5 Composer

```xml
<Border Background="#0B0D10" Padding="10" 
        BorderBrush="#2A3040" BorderThickness="0,1,0,0">
    <StackPanel Spacing="6">
        <TextBox Text="{Binding Text}" AcceptsReturn="True" 
                 MinHeight="40" MaxHeight="120"
                 Background="#1A1D24" Foreground="#E6E6E6" />
        <DockPanel>
            <TextBlock DockPanel.Dock="Left" Text="{Binding Error}" 
                       Foreground="#FFB4B4"
                       IsVisible="{Binding Error, Converter=...}" />
            <Button DockPanel.Dock="Right" Content="Send" 
                    Command="{Binding SendCommand}" 
                    Background="#3B82F6" Foreground="#FFFFFF" />
        </DockPanel>
    </StackPanel>
</Border>
```

**Rules:**
- Top border only (1px) to separate from transcript
- Multi-line input: `AcceptsReturn="True"`, `MaxHeight="120"` (auto-grow, then scroll)
- Error inline, left-aligned, red text
- Send button always visible, disabled when empty

### 5.6 Error States

**Inline error (forms):**
```xml
<Border Background="#2A1515" CornerRadius="4" Padding="8" 
        BorderBrush="#5C2020" BorderThickness="1">
    <TextBlock Text="{Binding Error}" Foreground="#FFB4B4" FontSize="12" />
</Border>
```

**Status bar error:** Same text color (`#FFB4B4`), no container.

---

## 6. Interaction Guidelines

### 6.1 Expand/Collapse Rules

| State | Behavior |
|-------|----------|
| New intent group | Expanded by default |
| Group with running tools | Stay expanded |
| All tools completed | Auto-collapse (if setting enabled) |
| User manually collapsed | Respect until session ends |

### 6.2 Tooltips vs Expanders vs Flyouts

| Content Type | Mechanism |
|--------------|-----------|
| Short metadata (<50 chars) | Tooltip |
| Multi-line preview | Inline expander |
| Full JSON / scrollable content | Flyout (side panel) |

**Current implementation:** Flyout for tool details (correct choice for JSON content).

### 6.3 Copy Feedback

On successful copy:
1. Button text changes: "Copy input" → "Copied!" (0.8s)
2. Return to original text

Future enhancement: Toast notification for accessibility.

### 6.4 Keyboard Shortcuts

| Action | Shortcut |
|--------|----------|
| Send message | `Ctrl+Enter` or `Enter` (single-line mode) |
| Focus composer | `/` or `Ctrl+L` |
| Expand all groups | `Ctrl+Shift+E` |
| Collapse all groups | `Ctrl+Shift+C` |
| Close flyout | `Escape` |

(Many not yet implemented—document for future work.)

### 6.5 Smart Scroll Rules

**Transcript auto-scroll:**
- If user is within 50px of bottom: auto-scroll on new content
- If user scrolled up: pause auto-scroll (preserve reading position)
- New user message: always scroll to bottom

**Implementation pattern:**
```csharp
private void OnTranscriptChanged()
{
    var extent = _scrollViewer.Extent.Height;
    var viewport = _scrollViewer.Viewport.Height;
    var offset = _scrollViewer.Offset.Y;
    var nearBottom = extent - offset - viewport < 50;
    
    if (nearBottom || _userJustSentMessage)
    {
        _scrollViewer.ScrollToEnd();
    }
}
```

---

## 7. Screenshot Test Guidance

### 7.1 What to Snapshot

| Category | Examples |
|----------|----------|
| **Screens** | `connect-screen.png`, `shell-chat.png` |
| **States** | `connect-screen-error.png`, `composer-error.png` |
| **Components** | `intent-group-expanded.png`, `tool-detail.png` |
| **Edge cases** | Empty states, long content truncation |

### 7.2 Naming Convention

```
{component}-{variant}.png

Examples:
  connect-screen.png
  connect-screen-error.png
  intent-group-expanded.png
  intent-group-collapsed.png
  tool-detail.png
```

### 7.3 Stable Layout Rules (Reduce Churn)

1. **Fixed dimensions:** Always specify `width` and `height` in `ScreenshotTestHarness.Save()`
2. **Deterministic data:** Use static test data, not live API responses
3. **No timestamps:** If showing timestamps, mock to fixed value
4. **No animations:** Ensure all animations complete before capture

**Test pattern:**
```csharp
[AvaloniaFact]
public void Renders_tool_detail_with_json()
{
    var vm = new ToolCallDetailViewModel
    {
        Title = "read_file",
        Status = "completed",
        RawInputJson = "{ \"path\": \"README.md\" }",
        RawOutputJson = "{ \"content\": \"# Hello\" }",
    };

    ScreenshotTestHarness.Save(
        new ToolCallDetailView { DataContext = vm },
        width: 500, 
        height: 300, 
        fileName: "tool-detail.png"
    );
}
```

### 7.4 Harness Best Practices

- Use `Window` wrapper (controls must attach to visual root)
- Set `Background = Brushes.Black` on both Window and wrapper Border
- Call `Dispatcher.UIThread.RunJobs()` after show and after layout
- Close window after capture to avoid leaks

---

## 8. Don'ts

### 8.1 Crowding

❌ **Don't** stack more than 3 preview lines per tool row  
✅ **Do** use single-line truncated previews + flyout for full content

❌ **Don't** show full JSON inline in the transcript  
✅ **Do** show a preview; full content lives in flyout

❌ **Don't** use icons + text + badges on every row  
✅ **Do** pick one secondary indicator (status dot suffices)

### 8.2 Inconsistency

❌ **Don't** mix hex colors inline—use defined tokens  
❌ **Don't** use different corner radii for same-level containers  
❌ **Don't** vary spacing randomly (8px here, 10px there)

### 8.3 Over-Disclosure

❌ **Don't** expand all intent groups by default  
❌ **Don't** auto-open flyouts  
❌ **Don't** show error stack traces inline (use flyout or log)

### 8.4 Visual Noise

❌ **Don't** use colored backgrounds for status (dot is enough)  
❌ **Don't** add shadows in dark theme (they're invisible or harsh)  
❌ **Don't** animate every state change (reserve for user-initiated actions)

---

## 9. Future Considerations

### 9.1 Resource Dictionary Migration

Move inline colors to `Styles/Colors.axaml`:
```xml
<ResourceDictionary>
    <Color x:Key="BgBaseColor">#0B0D10</Color>
    <SolidColorBrush x:Key="BgBase" Color="{StaticResource BgBaseColor}" />
</ResourceDictionary>
```

### 9.2 Theme Support

When adding light theme:
1. Define semantic tokens (not raw hex in views)
2. Use `ThemeVariant` resources
3. Maintain same hierarchy and density

### 9.3 Accessibility

- Minimum contrast ratio: 4.5:1 for body text
- Focus indicators on all interactive elements
- Screen reader labels for status dots

---

## Appendix: Quick Reference Card

```
BACKGROUNDS
  Base:      #0B0D10  (chrome, flyouts)
  Primary:   #111318  (main area)
  Secondary: #1A1D24  (cards, inputs)
  Tertiary:  #151820  (nested rows)

TEXT
  Primary:   #E6E6E6
  Secondary: #B9C0CC
  Muted:     #6B7280
  Error:     #FFB4B4

STATUS DOTS
  Running:   #FBBF24 (yellow)
  Success:   #22C55E (green)
  Error:     #EF4444 (red)

SPACING
  Base unit: 4px
  Common:    4, 8, 12, 16, 20

TYPOGRAPHY
  Body:   14px, Normal
  Code:   11-12px, Consolas
  Header: 14-18px, Bold/SemiBold

RADIUS
  Small:  4px (badges, rows)
  Medium: 6px (cards, bubbles)
  Large:  8px (flyouts)
```
