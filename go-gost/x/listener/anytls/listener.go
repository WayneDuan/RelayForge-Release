package anytls

import (
	"context"
	"net"
	"time"

	"github.com/go-gost/core/listener"
	"github.com/go-gost/core/logger"
	md "github.com/go-gost/core/metadata"
	"github.com/go-gost/x/internal/anytls"
	"github.com/go-gost/x/internal/net/proxyproto"
	"github.com/go-gost/x/registry"
)

func init() {
	registry.ListenerRegistry().Register("anytls", NewListener)
}

type anytlsListener struct {
	ln      net.Listener
	logger  logger.Logger
	md      metadata
	options listener.Options
	server  *anytls.Server
	streams chan net.Conn
	close   chan struct{}
}

func NewListener(opts ...listener.Option) listener.Listener {
	options := listener.Options{}
	for _, opt := range opts {
		opt(&options)
	}
	return &anytlsListener{logger: options.Logger, options: options, streams: make(chan net.Conn), close: make(chan struct{})}
}

func (l *anytlsListener) Init(md md.Metadata) error {
	if err := l.parseMetadata(md); err != nil {
		return err
	}
	if l.md.password == "" {
		return net.ErrClosed
	}
	ln, err := net.Listen("tcp", l.options.Addr)
	if err != nil {
		return err
	}
	l.ln = proxyproto.WrapListener(l.options.ProxyProtocol, ln, 10*time.Second)
	l.server = anytls.NewServer(l.md.password, l.options.TLSConfig)
	go l.acceptLoop()
	return nil
}

func (l *anytlsListener) acceptLoop() {
	for {
		conn, err := l.ln.Accept()
		if err != nil {
			select {
			case <-l.close:
				return
			default:
			}
			continue
		}
		go l.server.Handle(context.Background(), conn, func(stream net.Conn) {
			select {
			case l.streams <- stream:
			case <-l.close:
				_ = stream.Close()
			}
		})
	}
}

func (l *anytlsListener) Accept() (net.Conn, error) {
	select {
	case conn := <-l.streams:
		return conn, nil
	case <-l.close:
		return nil, net.ErrClosed
	}
}

func (l *anytlsListener) Addr() net.Addr { return l.ln.Addr() }

func (l *anytlsListener) Close() error {
	select {
	case <-l.close:
	default:
		close(l.close)
	}
	if l.ln != nil {
		return l.ln.Close()
	}
	return nil
}
