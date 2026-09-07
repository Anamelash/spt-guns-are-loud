cmake_minimum_required(VERSION 3.20)
project(GalHeadphoneAudioPlugin LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED UNITY_AUDIO_SDK_DIR)
  set(UNITY_AUDIO_SDK_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../.deps/NativeAudioPlugins/NativeCode")
endif()

add_library(AudioPluginGalHeadphones SHARED src/GalHeadphoneAudioPlugin.cpp src/GalHeadphoneDsp.cpp)
target_include_directories(AudioPluginGalHeadphones PRIVATE src "${UNITY_AUDIO_SDK_DIR}")
target_compile_definitions(AudioPluginGalHeadphones PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
if(MSVC)
  target_compile_options(AudioPluginGalHeadphones PRIVATE /W4 /permissive- /EHsc)
endif()

add_executable(GalHeadphoneDspTests tests/GalHeadphoneDspTests.cpp src/GalHeadphoneDsp.cpp)
target_include_directories(GalHeadphoneDspTests PRIVATE src)
if(MSVC)
  target_compile_options(GalHeadphoneDspTests PRIVATE /W4 /permissive- /EHsc)
endif()

add_executable(GalHeadphoneRegistrationTests tests/GalHeadphoneRegistrationTests.cpp)
target_include_directories(GalHeadphoneRegistrationTests PRIVATE "${UNITY_AUDIO_SDK_DIR}")

enable_testing()
add_test(NAME dsp COMMAND GalHeadphoneDspTests)
add_test(NAME registration COMMAND GalHeadphoneRegistrationTests $<TARGET_FILE:AudioPluginGalHeadphones>)
