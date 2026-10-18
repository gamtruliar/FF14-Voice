#pragma once
// The pinned engine's Windows _read path in llama-moe-slot.cpp uses these
// POSIX type names without defining them for MSVC. Apply only to that file.
#ifdef _MSC_VER
#include <cstddef>
#include <cstdint>
using ssize_t = std::intptr_t;
struct iovec {
    void * iov_base;
    std::size_t iov_len;
};
#endif
