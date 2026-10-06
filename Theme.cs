using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RIR_PluginManager
{
    /// Светлая минималистичная тема в духе архитектурных интерфейсов:
    /// светло-серый фон, графитовый текст, тонкие линии, мягкие тени,
    /// чёрное выделение, мятный акцент у тумблеров, оранжевый — как редкий маркер.
    internal static class Theme
    {
        static Color C(byte r, byte g, byte b, byte a = 0xFF) => Color.FromArgb(a, r, g, b);
        static Brush S(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static readonly Color Ink = C(0x1E, 0x1F, 0x22);          // основной текст, чёрные элементы
        public static readonly Color Muted = C(0x7A, 0x7F, 0x87);        // подписи, вторичный текст
        public static readonly Color Line = C(0xD3, 0xD6, 0xDA);         // тонкие линии и рамки
        public static readonly Color Paper = C(0xF7, 0xF7, 0xF5);        // панели
        public static readonly Color Mint = C(0x6F, 0xD6, 0xAE);         // акцент: включено
        public static readonly Color Accent = C(0xFF, 0x5A, 0x1F);       // оранжевый маркер

        public static readonly Brush Fg = S(Ink);
        public static readonly Brush FgDim = S(Muted);
        public static readonly Brush FgInverse = S(Colors.White);
        public static readonly Brush Danger = S(C(0xC0, 0x39, 0x2B));
        public static readonly Brush AccentBrush = S(Accent);

        public static readonly Brush Error = S(C(0xE0, 0x4F, 0x45));
        public static readonly Brush Warning = S(C(0xD9, 0x96, 0x00));
        public static readonly Brush Info = S(C(0x4F, 0x7F, 0xD9));
        public static readonly Brush Ok = S(C(0x2F, 0xA9, 0x7A));

        public static readonly Brush SelectedFill = S(Ink);              // выделенная строка — чёрная, текст белый
        public static readonly Brush SelectedBorder = S(Ink);
        public static readonly Brush PanelFill = S(Paper);
        public static readonly Brush ActiveFill = S(Ink);                // активный сегмент сортировки
        public static readonly Brush GlassBorder = S(Line);              // (имя сохранено) рамки панелей

        public static readonly Brush WindowBackground = MakeWindowBackground();

        static Brush MakeWindowBackground()
        {
            var b = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(C(0xEE, 0xEF, 0xF0), 0.0),
                new GradientStop(C(0xE6, 0xE8, 0xEA), 0.55),
                new GradientStop(C(0xDC, 0xDF, 0xE2), 1.0)
            }, new Point(0, 0), new Point(0, 1));
            b.Freeze();
            return b;
        }

        public static ResourceDictionary Load() => (ResourceDictionary)XamlReader.Parse(Xaml);

        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <DropShadowEffect x:Key='SoftShadow' BlurRadius='10' ShadowDepth='2' Direction='270' Opacity='0.12' Color='#000000'/>

  <!-- Обычная кнопка: светлая «пилюля» с тонкой рамкой и мягкой тенью -->
  <Style TargetType='Button'>
    <Setter Property='Foreground' Value='#1E1F22'/>
    <Setter Property='Background' Value='#FBFBFA'/>
    <Setter Property='BorderBrush' Value='#D3D6DA'/>
    <Setter Property='Padding' Value='14,6'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='16' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'
                  Padding='{TemplateBinding Padding}' Effect='{StaticResource SoftShadow}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='BorderBrush' Value='#1E1F22'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#ECEDEE'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Главная кнопка: чёрная «пилюля» с белым текстом -->
  <Style x:Key='PrimaryButton' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Foreground' Value='#FFFFFF'/>
    <Setter Property='Background' Value='#1E1F22'/>
    <Setter Property='BorderBrush' Value='#1E1F22'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='16' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'
                  Padding='{TemplateBinding Padding}' Effect='{StaticResource SoftShadow}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#3A3C41'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#000000'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Тумблер вместо галочки: светлая дорожка, белый кружок с тенью, мятный цвет во включённом состоянии -->
  <Style TargetType='CheckBox'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <StackPanel Orientation='Horizontal' Background='Transparent'>
            <Grid Width='38' Height='22' VerticalAlignment='Center'>
              <Border x:Name='track' CornerRadius='11' Background='#E1E3E6' BorderBrush='#CDD0D4' BorderThickness='1'/>
              <Ellipse x:Name='thumb' Width='16' Height='16' Fill='#FFFFFF'
                       HorizontalAlignment='Left' VerticalAlignment='Center' Margin='3,0,0,0'>
                <Ellipse.RenderTransform>
                  <TranslateTransform X='0'/>
                </Ellipse.RenderTransform>
                <Ellipse.Effect>
                  <DropShadowEffect BlurRadius='4' ShadowDepth='1' Direction='270' Opacity='0.25'/>
                </Ellipse.Effect>
              </Ellipse>
            </Grid>
            <ContentPresenter x:Name='content' Margin='10,0,0,0' VerticalAlignment='Center'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Trigger.EnterActions>
                <BeginStoryboard>
                  <Storyboard>
                    <DoubleAnimation Storyboard.TargetName='thumb'
                                     Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)'
                                     To='16' Duration='0:0:0.15'/>
                  </Storyboard>
                </BeginStoryboard>
              </Trigger.EnterActions>
              <Trigger.ExitActions>
                <BeginStoryboard>
                  <Storyboard>
                    <DoubleAnimation Storyboard.TargetName='thumb'
                                     Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)'
                                     To='0' Duration='0:0:0.15'/>
                  </Storyboard>
                </BeginStoryboard>
              </Trigger.ExitActions>
              <Setter TargetName='track' Property='Background' Value='#6FD6AE'/>
              <Setter TargetName='track' Property='BorderBrush' Value='#5CC79D'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='track' Property='BorderBrush' Value='#7A7F87'/>
            </Trigger>
            <Trigger Property='Content' Value='{x:Null}'>
              <Setter TargetName='content' Property='Margin' Value='0'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.45'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Тонкая полоса прокрутки -->
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='8'/>
    <Setter Property='MinWidth' Value='8'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border CornerRadius='4' Background='#B8BCC2' Margin='1,2'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='Background' Value='#1E1F22'/>
    <Setter Property='Foreground' Value='#FFFFFF'/>
    <Setter Property='BorderBrush' Value='#1E1F22'/>
  </Style>
</ResourceDictionary>";
    }
}
