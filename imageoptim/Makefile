HELPINDEXES=$(addsuffix Help.helpindex, $(wildcard *.lproj/Help/))
SUBMODULES=../Sparkle/Sparkle ../svgcleaner/src

# 签名身份可覆盖：CI 上没有 "Developer ID" 证书，硬编码会让 make 直接失败（exit 75）
CODESIGN_IDENTITY ?= Developer ID

build: all

all: $(SUBMODULES) pngout helpindexes

$(SUBMODULES): ../.gitmodules
	../scripts/fetch-submodule $(shell dirname $@) && touch $@

# 找不到指定身份时退到 ad-hoc 签名（codesign -s -），保证 arm64 上仍可执行
pngout:
	curl -fL --retry 3 -o pngout-20230322-mac.zip https://www.jonof.id.au/files/kenutils/pngout-20230322-mac.zip
	unzip -p pngout-20230322-mac.zip pngout-20230322-mac/pngout > pngout
	test -s pngout
	chmod a+rx pngout
	@if codesign -s "$(CODESIGN_IDENTITY)" ./pngout --force 2>/dev/null; then \
		echo "Signed pngout with '$(CODESIGN_IDENTITY)'"; \
	else \
		echo "No '$(CODESIGN_IDENTITY)' identity available, falling back to ad-hoc signature"; \
		codesign -s - ./pngout --force; \
	fi

clean:
	-rm -rf pngout pngout-20230322-mac.zip

install:

helpindexes:: $(HELPINDEXES)

%/Help.helpindex: %/index.html %/prefs.html
	./makehelp.sh $(patsubst %.lproj/Help/Help.helpindex,%,$@)

.PHONY: build all install clean
