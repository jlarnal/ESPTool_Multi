using EspDotNet.Communication;
using EspDotNet.Config;
using EspDotNet.Loaders.ESP32BootLoader;
using System.Text.RegularExpressions;
using System.Text;

namespace EspDotNet.Tools
{
    public class BootloaderTool
    {
        private readonly Communicator _communicator;
        private readonly List<List<PinSequenceStep>> _bootloaderSequences;

        public BootloaderTool(Communicator communicator, List<List<PinSequenceStep>> bootloaderSequences)
        {
            _communicator = communicator;
            _bootloaderSequences = bootloaderSequences;
        }

        public async Task<ESP32BootLoader> StartAsync(CancellationToken token = default)
        {
            foreach (var sequence in _bootloaderSequences)
            {
                _communicator.ClearBuffer();
                await _communicator.ExecutePinSequence(sequence, token);

                if (!await TryReadBootStartup(token))
                    continue;

                var bootloader = new ESP32BootLoader(_communicator);

                if (await Synchronize(bootloader, token))
                    return bootloader;
            }

            throw new Exception(
                $"Failed to start bootloader after trying {_bootloaderSequences.Count} reset sequence(s)");
        }

        private async Task<bool> TryReadBootStartup(CancellationToken token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(1000);

            try
            {
                var buffer = new byte[4096];
                var read = await _communicator.ReadRawAsync(buffer, cts.Token);
                if (read > 0)
                {
                    var data = new byte[read];
                    Array.Copy(buffer, data, read);
                    Regex regex = new Regex("boot:(0x[0-9a-fA-F]+)(.*waiting for download)?");
                    var result = regex.Match(Encoding.ASCII.GetString(data));
                    return result.Success;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timeout reading boot message — this sequence didn't work
            }

            return false;
        }

        private async Task<bool> Synchronize(ESP32BootLoader loader, CancellationToken token)
        {
            for (int tryNo = 0; tryNo < 100; tryNo++)
            {
                token.ThrowIfCancellationRequested();

                // Try to sync for 100ms.
                using CancellationTokenSource cts = new CancellationTokenSource();

                // Register the token and store the registration to dispose of it later
                using CancellationTokenRegistration ctr = token.Register(() => cts.Cancel());

                cts.CancelAfter(100); // Cancel after 100ms

                try
                {
                    if(await loader.SynchronizeAsync(cts.Token))
                        return true;
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    ctr.Unregister();
                }
            }

            return false;
        }
    }
}
