# Inject a per-source Windows compatibility header without editing upstream.
set(FF14_GGUF_COMPAT_DIR "${CMAKE_CURRENT_LIST_DIR}")
function(ff14_apply_windows_compat)
    if(MSVC AND TARGET llama)
        set_property(SOURCE "${CMAKE_SOURCE_DIR}/src/llama-moe-slot.cpp"
            TARGET_DIRECTORY llama APPEND PROPERTY COMPILE_OPTIONS
            "/FI${FF14_GGUF_COMPAT_DIR}/windows-compat.h")
    endif()
endfunction()
cmake_language(DEFER CALL ff14_apply_windows_compat)
