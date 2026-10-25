using Tattlebun.Hook;
using Tattlebun.Windows;

return HookRun.Run(Console.OpenStandardInput(), ReceiptStore.DefaultPath, (fingerprinter, previous) => new SnapshotReader(fingerprinter).Read(previous));
