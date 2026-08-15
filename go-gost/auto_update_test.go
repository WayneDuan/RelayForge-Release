package main

import "testing"

func TestCompareVersions(t *testing.T) {
	tests := []struct {
		left, right string
		want        int
	}{
		{left: "1.2.5", right: "1.2.4", want: 1},
		{left: "v1.2.4", right: "1.2.4", want: 0},
		{left: "1.2", right: "1.2.1", want: -1},
	}
	for _, test := range tests {
		if got := compareVersions(test.left, test.right); got != test.want {
			t.Errorf("compareVersions(%q, %q) = %d, want %d", test.left, test.right, got, test.want)
		}
	}
}

func TestResolveAssetURL(t *testing.T) {
	got, err := resolveAssetURL("https://updates.example.test/latest/agent-manifest.json", "gost-amd64")
	if err != nil || got != "https://updates.example.test/latest/gost-amd64" {
		t.Fatalf("resolveAssetURL returned %q, %v", got, err)
	}
	if _, err := resolveAssetURL("https://updates.example.test/latest/agent-manifest.json", "http://updates.example.test/gost-amd64"); err == nil {
		t.Fatal("resolveAssetURL accepted an HTTP asset URL")
	}
}
