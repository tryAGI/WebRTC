// Authored, diagnostic-only process activity control. ARC owns the token through
// the final consumer and endActivity; there is no global/persistent power change.
#define _DARWIN_C_SOURCE
#import <Foundation/Foundation.h>
#define main timers_main
#include "timers.c"
#undef main

int main(void)
{
    @autoreleasepool {
        const char *requested = getenv("TRYAGI_NATIVE_ACTIVITY");
        if (!requested) {
            puts("# native_activity=off");
            return timers_main();
        }
        if (strcmp(requested, "latency-critical")) return 8;
        NSProcessInfo *process = [NSProcessInfo processInfo];
        id<NSObject> token = [process beginActivityWithOptions:(NSActivityLatencyCritical | NSActivityUserInitiated)
                                                      reason:@"Synthetic timer diagnostic"];
        if (!token) return 9;
        puts("# native_activity=latency-critical");
        int result = timers_main();
        [process endActivity:token];
        return result;
    }
}
