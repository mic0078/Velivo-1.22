using System.Windows;
using System.Windows.Markup;

namespace Velivo
{
    // 🎨 nowoczesny wygląd (makieta zatwierdzona przez właściciela 2026-11-15): cienkie suwaki z fioletowym wypełnieniem i okrągłym uchwytem, przełącznik zamiast kratki
    static class AbStyl
    {
        const string X = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Fill' TargetType='RepeatButton'><Setter Property='Focusable' Value='False'/><Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='RepeatButton'><Border Background='{TemplateBinding Background}' CornerRadius='2'/></ControlTemplate></Setter.Value></Setter></Style>
  <Style x:Key='Knob' TargetType='Thumb'><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Thumb'>
    <Grid Width='16' Height='16'><Ellipse Fill='#557C5CFF' Margin='-3'/><Ellipse Fill='White'/></Grid></ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType='Slider'>
    <Setter Property='Foreground' Value='#7C5CFF'/><Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>
      <Grid Height='18' Background='Transparent'><Track x:Name='PART_Track' VerticalAlignment='Center'>
        <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource Fill}' Height='4' Background='{TemplateBinding Foreground}' Command='Slider.DecreaseLarge'/></Track.DecreaseRepeatButton>
        <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource Fill}' Height='4' Background='#2A2E3D' Command='Slider.IncreaseLarge'/></Track.IncreaseRepeatButton>
        <Track.Thumb><Thumb Style='{StaticResource Knob}'/></Track.Thumb></Track></Grid>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers><Trigger Property='Orientation' Value='Vertical'><Setter Property='Foreground' Value='#2ED3C6'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>
      <Grid Width='18' Background='Transparent'><Track x:Name='PART_Track' HorizontalAlignment='Center'>
        <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource Fill}' Width='5' Background='{TemplateBinding Foreground}' Command='Slider.DecreaseLarge'/></Track.DecreaseRepeatButton>
        <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource Fill}' Width='5' Background='#2A2E3D' Command='Slider.IncreaseLarge'/></Track.IncreaseRepeatButton>
        <Track.Thumb><Thumb Style='{StaticResource Knob}'/></Track.Thumb></Track></Grid>
    </ControlTemplate></Setter.Value></Setter></Trigger></Style.Triggers>
  </Style>
  <Style x:Key='Sw' TargetType='CheckBox'><Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
    <Border x:Name='b' Width='38' Height='20' CornerRadius='10' Background='#2A2E3D'><Ellipse x:Name='k' Width='14' Height='14' Fill='White' HorizontalAlignment='Left' Margin='3,0'/></Border>
    <ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='Background' Value='#7C5CFF'/><Setter TargetName='k' Property='HorizontalAlignment' Value='Right'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>";
        static ResourceDictionary d;
        public static ResourceDictionary Res { get { if (d == null) { try { d = (ResourceDictionary)XamlReader.Parse(X); } catch (System.Exception ex) { App.LogError(ex); d = new ResourceDictionary(); } } return d; } }   // błąd wyglądu nie może zamknąć programu - wtedy zwykłe suwaki
        public static Style Sw => Res.Contains("Sw") ? (Style)Res["Sw"] : null;
    }
}
