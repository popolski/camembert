using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Camembert.Core.Interfaces;
using Camembert.Core.Models;
using Camembert.Core.Services;

namespace Camembert.Windows.Elevation;

/// <summary>
/// Analyse exécutée dans un second processus, élevé, pour que l'interface reste sans privilèges.
/// Protocole minimal sur un canal nommé créé par l'interface : le processus élevé n'envoie que
/// des trames « progression », puis soit l'arbre, soit un message d'erreur. Il ne reçoit aucune
/// commande : la fermeture du canal vaut annulation.
/// </summary>
public static class ElevatedScan
{
    public const string Switch = "--analyse-elevee";
    const byte ProgressFrame = (byte)'P', TreeFrame = (byte)'T', ErrorFrame = (byte)'E';

    /// <summary>Côté processus élevé : analyse <paramref name="rootPath"/> et envoie le résultat. Renvoie le code de sortie.</summary>
    public static int RunHost(IScanEngine engine, string rootPath, string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(10_000);
        using var cancellation = new CancellationTokenSource();
        // Toute lecture qui se termine (canal fermé par l'interface) annule l'analyse.
        _ = pipe.ReadAsync(new byte[1]).AsTask().ContinueWith(_ => cancellation.Cancel(), TaskScheduler.Default);

        var progress = new ScanProgress();
        var writer = new BinaryWriter(new BufferedStream(pipe, 1 << 20), Encoding.UTF8);
        var writeLock = new object();
        using var timer = new Timer(_ =>
        {
            if (!Monitor.TryEnter(writeLock)) return;
            try { WriteProgress(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { cancellation.Cancel(); }
            finally { Monitor.Exit(writeLock); }
        }, null, 100, 100);

        try
        {
            var tree = engine.Scan(rootPath, progress, cancellation.Token);
            lock (writeLock)
            {
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                WriteProgress();
                writer.Write(TreeFrame);
                writer.Flush();
                tree.Write(writer.BaseStream);
                writer.Flush();
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 2;
        }
        catch (Exception e)
        {
            try
            {
                lock (writeLock)
                {
                    writer.Write(ErrorFrame);
                    writer.Write(ErrorText.Describe(e));
                    writer.Flush();
                }
            }
            catch (Exception io) when (io is IOException or ObjectDisposedException) { }
            return 1;
        }

        void WriteProgress()
        {
            writer.Write(ProgressFrame);
            writer.Write(Interlocked.Read(ref progress.Files));
            writer.Write(Interlocked.Read(ref progress.Directories));
            writer.Write(Interlocked.Read(ref progress.Bytes));
            writer.Write(progress.CurrentPath);
            writer.Flush();
        }
    }

    /// <summary>
    /// Côté interface : relance <paramref name="executable"/> avec élévation et attend son résultat.
    /// Lève <see cref="UnauthorizedAccessException"/> si l'élévation est refusée,
    /// <see cref="InvalidOperationException"/> si l'analyse élevée échoue.
    /// </summary>
    public sealed class Client(string executable, string engineName) : IScanEngine
    {
        public string Name => engineName;

        public FileTree Scan(string rootPath, ScanProgress progress, CancellationToken cancellation)
        {
            string pipeName = "Camembert-" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            Process process;
            try
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
                start.ArgumentList.Add(Switch);
                start.ArgumentList.Add(rootPath);
                start.ArgumentList.Add(pipeName);
                process = Process.Start(start) ?? throw new InvalidOperationException("Le processus d'analyse n'a pas démarré.");
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED : refus dans la fenêtre UAC
            {
                throw new UnauthorizedAccessException("Élévation refusée.");
            }

            using (process)
            using (cancellation.Register(pipe.Dispose)) // fermer le canal annule le processus élevé
            {
                try
                {
                    var connection = pipe.WaitForConnectionAsync(cancellation);
                    while (!connection.Wait(200))
                        if (process.HasExited) throw new InvalidOperationException("Le processus d'analyse s'est arrêté avant de répondre.");

                    var reader = new BinaryReader(new BufferedStream(pipe, 1 << 20), Encoding.UTF8);
                    while (true)
                    {
                        switch (reader.ReadByte())
                        {
                            case ProgressFrame:
                                Interlocked.Exchange(ref progress.Files, reader.ReadInt64());
                                Interlocked.Exchange(ref progress.Directories, reader.ReadInt64());
                                Interlocked.Exchange(ref progress.Bytes, reader.ReadInt64());
                                progress.CurrentPath = reader.ReadString();
                                break;
                            case TreeFrame:
                                progress.CurrentPath = "Réception des résultats…";
                                return FileTree.Read(reader.BaseStream);
                            case ErrorFrame:
                                throw new InvalidOperationException(reader.ReadString());
                            default:
                                throw new InvalidDataException("Réponse inattendue du processus d'analyse.");
                        }
                    }
                }
                catch (Exception e) when (cancellation.IsCancellationRequested && e is not OperationCanceledException)
                {
                    throw new OperationCanceledException(cancellation);
                }
                catch (Exception e) when (e is EndOfStreamException or IOException or AggregateException)
                {
                    throw new InvalidOperationException("Le processus d'analyse s'est interrompu.", e);
                }
            }
        }
    }
}
