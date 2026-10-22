# Xcode 的 SVGO legacy target 传的是 "make build"，所以需要显式的 build 目标
build: all

all: ./build/svgo.js

./build/svgo.js: ./node_modules/.bin/webpack index.js webpack.config.js
	./node_modules/.bin/webpack

# --include=dev：CI/本地若带 NODE_ENV=production，npm 会跳过 devDependencies（webpack 就装不上）
./node_modules/.bin/webpack: package.json
	npm install --include=dev && touch -c ./node_modules/.bin/webpack

clean:
	-rm -rf build node_modules

.PHONY: build clean
