#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stddef.h>
int main() {
    void* handle = dlopen("libleptonica-1.82.0.so", RTLD_NOW);
    if (!handle) {
        printf("dlopen failed: %s\n", dlerror());
        return 1;
    }
    printf("dlopen succeeded\n");
    dlclose(handle);
    return 0;
}


