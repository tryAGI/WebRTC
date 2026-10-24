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
#include <string.h>
#ifdef __APPLE__
#include <pthread/qos.h>
#include <sys/qos.h>
#endif

static double microseconds(struct timespec end, struct timespec start)
{
    return (double)(end.tv_sec - start.tv_sec) * 1000000.0 +
        (double)(end.tv_nsec - start.tv_nsec) / 1000.0;
}

int main(void)
{
    const char *source = getenv("GITHUB_SHA");
    printf("# native_source=%s\n", source ? source : "working_tree_unverified");
    const char *requested_qos = getenv("TRYAGI_NATIVE_QOS");
    if (requested_qos && strcmp(requested_qos, "user-initiated")) return 4;
#ifdef __APPLE__
    if (requested_qos && pthread_set_qos_class_self_np(QOS_CLASS_USER_INITIATED, 0)) return 5;
    if (requested_qos && qos_class_self() != QOS_CLASS_USER_INITIATED) return 6;
    printf("# native_qos_requested=%s;observed=%u\n", requested_qos ? requested_qos : "default", (unsigned)qos_class_self());
#else
    if (requested_qos) return 4;
    puts("# native_qos_requested=default;observed=not_darwin");
#endif
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
