using System;
using NAudio.CoreAudioApi;

namespace Island
{
    // Системная громкость через NAudio (Core Audio).
    // Своих [ComImport]-классов тут быть не должно: второй COM-класс с тем же CLSID, что и у NAudio,
    // ломает MMDeviceEnumerator в NAudio (InvalidCastException), а с ним запись звука и эквалайзер.
    static class VolumeControl
    {
        // 0..1, либо -1 если получить не удалось
        public static float Get()
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                using (var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    return dev.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
            catch { return -1; }
        }

        public static void Set(float v)
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                using (var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    dev.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Max(0f, Math.Min(1f, v));
            }
            catch { }
        }
    }
}
