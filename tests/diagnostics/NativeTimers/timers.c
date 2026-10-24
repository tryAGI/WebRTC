// Authored diagnostic fixture, MIT. No transport or cryptography.
// Keep native sleep and independent clock observations on the same runner.
#ifdef __APPLE__
#define _DARWIN_C_SOURCE
#endif
#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

static double microseconds(struct timespec end, struct timespec start)
{
    return (double)(end.tv_sec - start.tv_sec) * 1000000.0 +
        (double)(end.tv_nsec - start.tv_nsec) / 1000.0;
}

int main(void)
{
    const char *source = getenv("GITHUB_SHA");
    printf("# native_source=%s\n", source ? source : "working_tree_unverified");
    puts("timer,sample,requested_us,elapsed_us,wall_elapsed_us,monotonic_elapsed_us");
    for (int sample = 0; sample < 100; sample++) {
        struct timespec raw_start, wall_start, mono_start, raw_end, wall_end, mono_end;
        struct timespec delay = { .tv_sec = 0, .tv_nsec = 20000000 };
        if (clock_gettime(CLOCK_REALTIME, &wall_start) || clock_gettime(CLOCK_MONOTONIC, &mono_start) ||
            clock_gettime(CLOCK_MONOTONIC_RAW, &raw_start)) return 2;
        while (nanosleep(&delay, &delay)) { if (errno != EINTR) return 3; }
        if (clock_gettime(CLOCK_MONOTONIC_RAW, &raw_end) || clock_gettime(CLOCK_MONOTONIC, &mono_end) ||
            clock_gettime(CLOCK_REALTIME, &wall_end)) return 2;
        printf("native-sleep,%d,20000,%.3f,%.3f,%.3f\n", sample,
            microseconds(raw_end, raw_start), microseconds(wall_end, wall_start), microseconds(mono_end, mono_start));
    }
    return 0;
}
