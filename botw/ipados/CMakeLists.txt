# BOTW-iPad: the BOTW-PC Switch runtime (NativeAOT, Ryujinx.dylib) in a native iPadOS app.
#   botw/ipados/build.sh builds the runtime and this project; or by hand:
#   cmake -S botw/ipados -B build-ipados -G Xcode -DBOTWPC_RUNTIME_DIR=<dir with Ryujinx.dylib> -DBOTWPC_IOS_DEPS=<.tools/ios/out>
cmake_minimum_required(VERSION 3.25)
project(botw_ipad LANGUAGES C OBJC)
set(CMAKE_OSX_ARCHITECTURES arm64)
set(CMAKE_OSX_DEPLOYMENT_TARGET 17.0)
set(BOTWPC_TEAM "" CACHE STRING "Apple development team ID")
set(BOTWPC_BUNDLE_ID "local.botwpc.ipad" CACHE STRING "Bundle identifier")
set(BOTWPC_RUNTIME_DIR "" CACHE PATH "NativeAOT publish dir containing Ryujinx.dylib (ios-arm64)")
set(BOTWPC_IOS_DEPS "" CACHE PATH "iOS dependencies built from source (MoltenVK/libMoltenVK.dylib)")
option(BOTWPC_INCREASED_MEMORY "sign with com.apple.developer.kernel.increased-memory-limit" ON)
set(BOTWPC_AOT_DIR "" CACHE PATH "ahead-of-time image (aot_build.py output with botw_aot.s); empty: JIT only")

set(RUNTIME ${BOTWPC_RUNTIME_DIR}/Ryujinx.dylib)
set(MOLTENVK ${BOTWPC_IOS_DEPS}/MoltenVK/libMoltenVK.dylib)

set(EMBED ${RUNTIME} ${MOLTENVK})
add_executable(BOTWiPad MACOSX_BUNDLE main.m GameViewController.m TouchControls.m SettingsView.m GameData.m Jit.c SignalHandler.c)
# The ahead-of-time image (hundreds of MB of code) is its own dylib: inside the executable it pushes the app's calls to
# its stubs out of ARM64 branch range.
if(BOTWPC_AOT_DIR)
  enable_language(ASM)
  add_library(BotwAot SHARED ${BOTWPC_AOT_DIR}/botw_aot.s)
  set_target_properties(BotwAot PROPERTIES
    XCODE_ATTRIBUTE_CODE_SIGN_STYLE Automatic
    XCODE_ATTRIBUTE_DEVELOPMENT_TEAM "${BOTWPC_TEAM}"
    XCODE_ATTRIBUTE_LD_DYLIB_INSTALL_NAME "@rpath/libBotwAot.dylib"
    XCODE_ATTRIBUTE_IPHONEOS_DEPLOYMENT_TARGET 17.0)
  target_link_libraries(BOTWiPad PRIVATE BotwAot)
  list(APPEND EMBED BotwAot)
  target_compile_definitions(BOTWiPad PRIVATE BOTWPC_AOT=1)
endif()
target_include_directories(BOTWiPad PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(BOTWiPad PRIVATE ${RUNTIME} ${MOLTENVK} "-framework UIKit" "-framework QuartzCore" "-framework Metal"
                      "-framework GameController" "-framework CoreMotion" "-framework CoreHaptics" "-framework CoreGraphics" "-framework Foundation")
set_target_properties(BOTWiPad PROPERTIES
  OUTPUT_NAME BOTWiPad
  MACOSX_BUNDLE_INFO_PLIST ${CMAKE_CURRENT_SOURCE_DIR}/Info.plist.in
  MACOSX_BUNDLE_GUI_IDENTIFIER ${BOTWPC_BUNDLE_ID}
  XCODE_ATTRIBUTE_PRODUCT_BUNDLE_IDENTIFIER ${BOTWPC_BUNDLE_ID}
  XCODE_ATTRIBUTE_TARGETED_DEVICE_FAMILY 2
  XCODE_ATTRIBUTE_CODE_SIGN_STYLE Automatic
  XCODE_ATTRIBUTE_DEVELOPMENT_TEAM "${BOTWPC_TEAM}"
  XCODE_ATTRIBUTE_CLANG_ENABLE_OBJC_ARC YES
  XCODE_ATTRIBUTE_LD_RUNPATH_SEARCH_PATHS "@executable_path/Frameworks"
  XCODE_ATTRIBUTE_IPHONEOS_DEPLOYMENT_TARGET 17.0
  XCODE_EMBED_FRAMEWORKS "${EMBED}"
  XCODE_EMBED_FRAMEWORKS_CODE_SIGN_ON_COPY YES)
if(BOTWPC_INCREASED_MEMORY)
  set_target_properties(BOTWiPad PROPERTIES XCODE_ATTRIBUTE_CODE_SIGN_ENTITLEMENTS "${CMAKE_CURRENT_SOURCE_DIR}/BOTWiPad.entitlements")
endif()
