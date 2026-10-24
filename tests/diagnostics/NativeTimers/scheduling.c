// Authored diagnostic launcher, MIT. Changes only this child's process policy.
#define _DARWIN_C_SOURCE
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/resource.h>
#include <unistd.h>

static int background(void)
{
    errno = 0;
    int result = getpriority(PRIO_DARWIN_PROCESS, 0);
    if (errno) { perror("getpriority"); return -1; }
    return result;
}

int main(int argc, char **argv)
{
    if (argc < 3) return 2;
    int before = background();
    if (before < 0) return 3;
    if (!strcmp(argv[1], "background")) {
        if (setpriority(PRIO_DARWIN_PROCESS, 0, PRIO_DARWIN_BG)) { perror("setpriority"); return 4; }
    } else if (!strcmp(argv[1], "restored")) {
        if (setpriority(PRIO_DARWIN_PROCESS, 0, 0)) { perror("setpriority"); return 4; }
    } else if (strcmp(argv[1], "inherited")) return 2;
    int after = background();
    if (after < 0 || (!strcmp(argv[1], "background") && after != 1) ||
        (!strcmp(argv[1], "restored") && after != 0)) return 5;
    fprintf(stderr, "# scheduling_policy=%s;before=%d;after=%d\n", argv[1], before, after);
    fflush(stderr);
    execv(argv[2], argv + 2);
    perror("execv");
    return 6;
}
