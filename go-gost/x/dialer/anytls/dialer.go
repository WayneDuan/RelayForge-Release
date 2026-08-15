package anytls

import (
	"context"
	"net"
	"sync"
	"time"

	"github.com/go-gost/core/dialer"
	"github.com/go-gost/core/logger"
	md "github.com/go-gost/core/metadata"
	"github.com/go-gost/x/internal/anytls"
	"github.com/go-gost/x/registry"
)

func init() {
	registry.DialerRegistry().Register("anytls", NewDialer)
}

type anytlsDialer struct {
	md      metadata
	logger  logger.Logger
	options dialer.Options
	clients map[string]*anytls.Client
	clientsMu sync.Mutex
}

func NewDialer(opts ...dialer.Option) dialer.Dialer {
	options := dialer.Options{}
	for _, opt := range opts {
		opt(&options)
	}
	return &anytlsDialer{logger: options.Logger, options: options, clients: make(map[string]*anytls.Client)}
}

func (d *anytlsDialer) Init(md md.Metadata) error {
	return d.parseMetadata(md)
}

func (d *anytlsDialer) Multiplex() bool { return true }

func (d *anytlsDialer) Dial(ctx context.Context, addr string, opts ...dialer.DialOption) (net.Conn, error) {
	options := dialer.DialOptions{}
	for _, opt := range opts {
		opt(&options)
	}
	if options.Dialer == nil {
		return nil, net.ErrClosed
	}
	d.clientsMu.Lock()
	client := d.clients[addr]
	if client == nil {
		client = anytls.NewClient(d.md.password, d.options.TLSConfig, func(ctx context.Context) (net.Conn, error) {
			return options.Dialer.Dial(ctx, "tcp", addr)
		})
		d.clients[addr] = client
	}
	d.clientsMu.Unlock()
	return client.CreateProxy(ctx, addr)
}

// Handshake is intentionally a no-op. AnyTLS creates its stream and sends
// the destination address in Dial, before GOST's connector is invoked.
func (d *anytlsDialer) Handshake(ctx context.Context, conn net.Conn, options ...dialer.HandshakeOption) (net.Conn, error) {
	if d.md.handshakeTimeout > 0 {
		_ = conn.SetDeadline(time.Now().Add(d.md.handshakeTimeout))
		defer conn.SetDeadline(time.Time{})
	}
	return conn, nil
}
