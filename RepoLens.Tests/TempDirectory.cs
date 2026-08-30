namespace DevContext.Tests;

internal static class TempDirectory
{
    /// <summary>
    /// Deletes a directory that a child process was using as its working directory.
    ///
    /// Windows holds a handle on a process's current directory until the kernel has finished tearing
    /// the process down, and that happens after the process has exited or been killed — so a delete
    /// issued immediately afterwards races the teardown and fails with "the process cannot access
    /// the file ... because it is being used by another process".
    ///
    /// The race is timing-dependent, which is the worst kind of test failure: every test here passed
    /// locally and two of them failed on the Windows CI leg, in cleanup, after their assertions had
    /// already succeeded. Waiting for the handle to be released is the fix; assuming the cleanup is
    /// synchronous is the bug.
    /// </summary>
    public static void Delete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  && attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }
}
