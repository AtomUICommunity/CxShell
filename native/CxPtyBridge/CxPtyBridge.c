/*
 * CxPtyBridge - minimal native shim used by the Unix local terminal.
 *
 * The .NET runtime cannot return to managed code inside a forked child
 * (https://github.com/dotnet/runtime/issues/12350), and the JIT-ed pages are
 * not inherited by the child at all on Apple platforms. The child therefore
 * has to chdir/execve entirely in native code, exactly like the runtime's own
 * SystemNative_ForkAndExecProcess shim. Doing the whole fork here also means
 * the C compiler handles the platform ABI, including the variadic ioctl on
 * Apple arm64.
 *
 * Build: tools/build-pty-bridge.sh
 */

#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif

#include <errno.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <unistd.h>

#if defined(_WIN32)
#error "CxPtyBridge is only used on Linux and macOS."
#endif

#if defined(__GNUC__) || defined(__clang__)
#define CX_PTY_EXPORT __attribute__((visibility("default")))
#else
#define CX_PTY_EXPORT
#endif

static void cx_pty_fill_window_size(struct winsize *window_size,
                                    unsigned short rows,
                                    unsigned short columns)
{
    window_size->ws_row = rows;
    window_size->ws_col = columns;
    window_size->ws_xpixel = 0;
    window_size->ws_ypixel = 0;
}

/*
 * Starts `executable` on a new pseudo-terminal.
 *
 * Returns the child pid (greater than zero) and stores the master file
 * descriptor in `master_out`, or returns -1 with errno set. The child never
 * returns to managed code: it only chdir/execve/_exit.
 */
CX_PTY_EXPORT int cx_pty_spawn(const char *executable,
                               char *const argv[],
                               char *const envp[],
                               const char *working_directory,
                               unsigned short rows,
                               unsigned short columns,
                               int *master_out)
{
    struct winsize window_size;
    int master = -1;
    pid_t child;

    if (executable == NULL || argv == NULL || master_out == NULL)
    {
        errno = EINVAL;
        return -1;
    }

    cx_pty_fill_window_size(&window_size, rows, columns);
    child = forkpty(&master, NULL, NULL, &window_size);
    if (child < 0)
        return -1;

    if (child == 0)
    {
        /* A failed chdir is not fatal: the shell still starts in the inherited
         * directory, which is what the previous managed implementation did. */
        if (working_directory != NULL && working_directory[0] != '\0')
        {
            int chdir_result = chdir(working_directory);
            (void)chdir_result;
        }

        if (envp != NULL)
            execve(executable, argv, envp);
        else
            execv(executable, argv);

        _exit(127);
    }

    *master_out = master;
    return (int)child;
}

/*
 * Applies TIOCSWINSZ to the master descriptor. Returns 0 on success, or -1
 * with errno set.
 */
CX_PTY_EXPORT int cx_pty_resize(int master, unsigned short rows, unsigned short columns)
{
    struct winsize window_size;

    cx_pty_fill_window_size(&window_size, rows, columns);
    return ioctl(master, TIOCSWINSZ, &window_size);
}
