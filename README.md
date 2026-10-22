# ImageOptim

[ImageOptim](https://imageoptim.com) is a GUI for lossless image optimization tools: Zopfli, PNGOUT, [OxiPNG](https://lib.rs/crates/oxipng), AdvPNG, PNGCrush, [JPEGOptim](https://github.com/tjko/jpegoptim), Jpegtran, [Guetzli](https://github.com/google/guetzli), [Gifsicle](https://kornel.ski/lossygif), [SVGO](https://github.com/svg/svgo), [svgcleaner](https://github.com/RazrFalcon/svgcleaner) and [MozJPEG](https://github.com/mozilla/mozjpeg).

## Building

Requires:

* Xcode
* [Rust](https://rust-lang.org/) installed via [rustup](https://www.rustup.rs/) (not Homebrew).

```sh
git clone --recursive https://imageoptim.com ImageOptim
cd ImageOptim
```

To get started, open `imageoptim/ImageOptim.xcodeproj`. It will automatically download and build all subprojects when run in Xcode.

If the first build fails with `Build input file cannot be found: .../zlib/trees.c` (or other subproject sources), just build again: Xcode 26 scans dependencies before the subproject download targets have produced the sources.

In case of build errors, these sometimes help:

```sh
git submodule update --init
```

```sh
cd gifsicle # or pngquant
make clean
make
```
