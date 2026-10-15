// Diagnostic timing harness: let a reader acquire the first new report before the writer locks it.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <sys/file.h>
#include <unistd.h>
#include <stdio.h>
#include <string.h>
#include <stdatomic.h>
static atomic_int armed = 0, grabbed = 0;
int flock(int fd, int op) {
    static int (*real_flock)(int,int);
    if (!real_flock) real_flock = dlsym(RTLD_NEXT, "flock");
    char proc[80], path[4096];
    snprintf(proc, sizeof(proc), "/proc/self/fd/%d", fd);
    ssize_t n = readlink(proc, path, sizeof(path)-1);
    if (n < 0) return real_flock(fd, op);
    path[n] = 0;
    int target = strstr(path, "/results/records_") && n > 6 && !strcmp(path+n-6, ".jsonl");
    if (target && (op & LOCK_EX) && !atomic_exchange(&armed, 1)) {
        for (int i=0; i<5000 && !atomic_load(&grabbed); i++) usleep(1000);
    }
    int result = real_flock(fd, op);
    if (target && (op & LOCK_SH) && result == 0) atomic_store(&grabbed, 1);
    return result;
}
