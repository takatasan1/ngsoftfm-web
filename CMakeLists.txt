# CMake definitions for SoftFM

cmake_minimum_required(VERSION 3.10)
project(SoftFM)

find_package(Threads)
find_package(PkgConfig)

# Boost is used for config parsing (Boost.Spirit) and is header-only in this project.
# Newer CMake versions may not ship FindBoost, so locate the headers directly.
find_path(BOOST_INCLUDE_DIR boost/spirit/include/qi.hpp)
if (BOOST_INCLUDE_DIR)
    message(STATUS "Found Boost headers: ${BOOST_INCLUDE_DIR}")
else()
    message(FATAL_ERROR "Can not find Boost headers (boost/spirit/include/qi.hpp). Install Boost development headers.\n"
                        "MSYS2 (UCRT64) example: pacman -S --needed mingw-w64-ucrt-x86_64-boost")
endif()

# Build options
option(SOFTFM_ENABLE_RTLSDR "Build RTL-SDR backend" ON)
option(SOFTFM_ENABLE_HACKRF "Build HackRF backend" ${UNIX})
option(SOFTFM_ENABLE_AIRSPY "Build Airspy backend" ${UNIX})
option(SOFTFM_ENABLE_BLADERF "Build BladeRF backend" ${UNIX})
option(SOFTFM_ENABLE_ALSA "Enable ALSA audio output (Linux only)" ${UNIX})

if (WIN32)
    # Keep the default Windows build minimal.
    set(SOFTFM_ENABLE_HACKRF OFF CACHE BOOL "" FORCE)
    set(SOFTFM_ENABLE_AIRSPY OFF CACHE BOOL "" FORCE)
    set(SOFTFM_ENABLE_BLADERF OFF CACHE BOOL "" FORCE)
    set(SOFTFM_ENABLE_ALSA OFF CACHE BOOL "" FORCE)
endif()

if (SOFTFM_ENABLE_ALSA)
    find_package(ALSA REQUIRED)
    add_compile_definitions(SOFTFM_HAS_ALSA=1)
endif()

# Find RTL-SDR library.
if (SOFTFM_ENABLE_RTLSDR)
    pkg_check_modules(PKG_RTLSDR librtlsdr)
    find_path(RTLSDR_INCLUDE_DIR rtl-sdr.h
              HINT ${PKG_RTLSDR_INCLUDE_DIRS})
    find_library(RTLSDR_LIBRARY NAMES rtlsdr librtlsdr librtlsdr.a
                 HINT ${PKG_RTLSDR_LIBRARY_DIRS})
endif()

# Find HackRF library.
if (SOFTFM_ENABLE_HACKRF)
    pkg_check_modules(PKG_HACKRF libhackrf)
    find_path(HACKRF_INCLUDE_DIR hackrf.h
              HINT ${PKG_HACKRF_INCLUDE_DIRS})
    find_library(HACKRF_LIBRARY NAMES hackrf libhackrf libhackrf.a
                 HINT ${PKG_HACKRF_LIBRARY_DIRS})
endif()

# Find Airspy library.
if (SOFTFM_ENABLE_AIRSPY)
    pkg_check_modules(PKG_AIRSPY libairspy)
    find_path(AIRSPY_INCLUDE_DIR airspy.h
              HINT ${PKG_AIRSPY_INCLUDE_DIRS})
    find_library(AIRSPY_LIBRARY NAMES airspy libairspy libairspy.a
                 HINT ${PKG_AIRSPY_LIBRARY_DIRS})
endif()

# Find BladeRF library.
if (SOFTFM_ENABLE_BLADERF)
    pkg_check_modules(PKG_BLADERF libbladerf)
    find_path(BLADERF_INCLUDE_DIR libbladeRF.h
              HINT ${PKG_BLADERF_INCLUDE_DIRS})
    find_library(BLADERF_LIBRARY NAMES bladeRF libbladeRF libbladeRF.so
                 HINT ${PKG_BLADERF_LIBRARY_DIRS})
endif()

# Find libusb
if (SOFTFM_ENABLE_RTLSDR OR SOFTFM_ENABLE_HACKRF OR SOFTFM_ENABLE_AIRSPY OR SOFTFM_ENABLE_BLADERF)
    pkg_check_modules(PKG_LIBUSB libusb-1.0)
    find_path(LIBUSB_INCLUDE_DIR libusb.h
              HINT ${PKG_LIBUSB_INCLUDE_DIRS}
              PATH_SUFFIXES libusb-1.0)
    find_library(LIBUSB_LIBRARY NAMES usb-1.0 libusb-1.0
                 HINT ${PKG_LIBUSB_LIBRARY_DIRS})
endif()

if(RTLSDR_INCLUDE_DIR AND RTLSDR_LIBRARY)
    message(STATUS "Found librtlsdr: ${RTLSDR_INCLUDE_DIR}, ${RTLSDR_LIBRARY}")
    add_compile_definitions(SOFTFM_HAS_RTLSDR=1)
else()
    if (SOFTFM_ENABLE_RTLSDR)
        message(WARNING "Can not find Osmocom RTL-SDR library")
        message("Try again with environment variable PKG_CONFIG_PATH")
        message("or with -DRTLSDR_INCLUDE_DIR=/path/rtlsdr/include")
        message("        -DRTLSDR_LIBRARY=/path/rtlsdr/lib/librtlsdr")
    endif()
endif()

set(RTLSDR_INCLUDE_DIRS ${RTLSDR_INCLUDE_DIR} ${LIBUSB_INCLUDE_DIR})
set(RTLSDR_LIBRARIES    ${RTLSDR_LIBRARY} ${LIBUSB_LIBRARY})

set(HACKRF_INCLUDE_DIRS ${HACKRF_INCLUDE_DIR} ${LIBUSB_INCLUDE_DIR})
set(HACKRF_LIBRARIES    ${HACKRF_LIBRARY} ${LIBUSB_LIBRARY})

set(AIRSPY_INCLUDE_DIRS ${AIRSPY_INCLUDE_DIR} ${LIBUSB_INCLUDE_DIR})
set(AIRSPY_LIBRARIES    ${AIRSPY_LIBRARY} ${LIBUSB_LIBRARY})

set(BLADERF_INCLUDE_DIRS ${BLADERF_INCLUDE_DIR} ${LIBUSB_INCLUDE_DIR})
set(BLADERF_LIBRARIES    ${BLADERF_LIBRARY} ${LIBUSB_LIBRARY})

# Compiler flags.
if (CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
    set(CMAKE_CXX_FLAGS "-Wall -std=c++11 -O2 -ffast-math -ftree-vectorize ${EXTRA_FLAGS}")
else()
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${EXTRA_FLAGS}")
endif()

set(sfmbase_SOURCES
    sfmbase/Filter.cpp
    sfmbase/FmDecode.cpp
    sfmbase/AudioOutput.cpp 
)

set(sfmbase_HEADERS
    include/AudioOutput.h
    include/Filter.h
    include/FmDecode.h
    include/MovingAverage.h
    include/Source.h
    include/SoftFM.h
    include/DataBuffer.h
    include/fastatan2.h
    include/parsekv.h
    include/util.h
)

# Base sources

set(sfmbase_SOURCES
    ${sfmbase_SOURCES}
    ${sfmbase_HEADERS}
)

# RTL-SDR sources

set(sfmrtlsdr_SOURCES
    sfmbase/RtlSdrSource.cpp
)

set(sfmrtlsdr_HEADERS
    include/RtlSdrSource.h
)

set(sfmrtlsdr_SOURCES
    ${sfmrtlsdr_SOURCES}
    ${sfmrtlsdr_HEADERS}
)

# HackRF sources

set(sfmhackrf_SOURCES
    sfmbase/HackRFSource.cpp
)

set(sfmhackrf_HEADERS
    include/HackRFSource.h
)

set(sfmhackrf_SOURCES
    ${sfmhackrf_SOURCES}
    ${sfmhackrf_HEADERS}
)

# Airspy sources

set(sfmairspy_SOURCES
    sfmbase/AirspySource.cpp
)

set(sfmairspy_HEADERS
    include/AirspySource.h
)

set(sfmairspy_SOURCES
    ${sfmairspy_SOURCES}
    ${sfmairspy_HEADERS}
)

# BLadeRF sources

set(sfmbladerf_SOURCES
    sfmbase/BladeRFSource.cpp
)

set(sfmbladerf_HEADERS
    include/BladeRFSource.h
)

set(sfmbladerf_SOURCES
    ${sfmbladerf_SOURCES}
    ${sfmbladerf_HEADERS}
)

# Libraries

add_library(sfmbase STATIC
    ${sfmbase_SOURCES}
)

if (SOFTFM_ENABLE_RTLSDR AND RTLSDR_INCLUDE_DIR AND RTLSDR_LIBRARY)
    add_library(sfmrtlsdr STATIC
        ${sfmrtlsdr_SOURCES}
    )
    target_include_directories(sfmrtlsdr PUBLIC
        ${RTLSDR_INCLUDE_DIRS}
    )
    target_link_libraries(sfmrtlsdr
        ${RTLSDR_LIBRARIES}
    )
endif()

if (SOFTFM_ENABLE_HACKRF AND HACKRF_LIBRARY)
    add_library(sfmhackrf STATIC
        ${sfmhackrf_SOURCES}
    )
    target_link_libraries(sfmhackrf
        ${HACKRF_LIBRARIES}
    )
    add_compile_definitions(SOFTFM_HAS_HACKRF=1)
endif()

if (SOFTFM_ENABLE_AIRSPY AND AIRSPY_LIBRARY)
    add_library(sfmairspy STATIC
        ${sfmairspy_SOURCES}
    )
    target_link_libraries(sfmairspy
        ${AIRSPY_LIBRARIES}
    )
    add_compile_definitions(SOFTFM_HAS_AIRSPY=1)
endif()

if (SOFTFM_ENABLE_BLADERF AND BLADERF_LIBRARY)
    add_library(sfmbladerf STATIC
        ${sfmbladerf_SOURCES}
    )
    target_link_libraries(sfmbladerf
        ${BLADERF_LIBRARIES}
    )
    add_compile_definitions(SOFTFM_HAS_BLADERF=1)
endif()

add_executable(softfm
	main.cpp
)

include_directories(
    ${CMAKE_SOURCE_DIR}/include
    ${EXTRA_INCLUDES} 
)

include_directories(${BOOST_INCLUDE_DIR})

if (SOFTFM_ENABLE_ALSA)
    include_directories(${ALSA_INCLUDE_DIRS})
endif()

target_link_libraries(softfm
    sfmbase
    ${CMAKE_THREAD_LIBS_INIT}
    ${EXTRA_LIBS} 
)

if (TARGET sfmrtlsdr)
    target_link_libraries(softfm sfmrtlsdr)
endif()
if (TARGET sfmhackrf)
    target_link_libraries(softfm sfmhackrf)
endif()
if (TARGET sfmairspy)
    target_link_libraries(softfm sfmairspy)
endif()
if (TARGET sfmbladerf)
    target_link_libraries(softfm sfmbladerf)
endif()

if (SOFTFM_ENABLE_ALSA)
    target_link_libraries(softfm ${ALSA_LIBRARIES})
endif()

install(TARGETS softfm DESTINATION bin)
install(TARGETS sfmbase DESTINATION lib)
if (TARGET sfmrtlsdr)
    install(TARGETS sfmrtlsdr DESTINATION lib)
endif()
if (TARGET sfmhackrf)
    install(TARGETS sfmhackrf DESTINATION lib)
endif()
if (TARGET sfmairspy)
    install(TARGETS sfmairspy DESTINATION lib)
endif()
if (TARGET sfmbladerf)
    install(TARGETS sfmbladerf DESTINATION lib)
endif()
