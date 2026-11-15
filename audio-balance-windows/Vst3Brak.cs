namespace Velivo
{
    // wtyczek VST3 w tym programie (jeszcze) nie ma - silnik pyta o nie, odpowiedź: brak
    static class Vst3 { public static bool Ready => false; public static bool Run(float[] buf, int frames) => false; }
}
