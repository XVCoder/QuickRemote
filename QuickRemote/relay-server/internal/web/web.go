package web

import (
	_ "embed"
	"net/http"
)

//go:embed static/about.html
var aboutHTML []byte

// AboutHandler 返回关于页面。
func AboutHandler(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Write(aboutHTML)
}
