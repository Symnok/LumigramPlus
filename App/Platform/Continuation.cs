using Windows.ApplicationModel.Activation;

namespace LumigramPlus.App
{
    /// <summary>
    /// A page that asked for a file and expects an answer.
    ///
    /// Windows Phone does not return from a file picker - it suspends the app,
    /// shows the picker, and activates the app again with the result. The page that
    /// asked may have been torn down and rebuilt in between, so the answer arrives
    /// at the application and has to be routed back to whoever is on screen now.
    /// That is what this interface is for.
    /// </summary>
    public interface IFileContinuation
    {
        /// <summary>A file was chosen to send, or the picker was cancelled.</summary>
        void FilePicked(FileOpenPickerContinuationEventArgs args);

        /// <summary>A destination was chosen to save to, or the picker was cancelled.</summary>
        void SaveLocationPicked(FileSavePickerContinuationEventArgs args);
    }
}
