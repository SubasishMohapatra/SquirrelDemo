﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NuGet.Client.V3Shim
{
    internal class ShimWebResponse : WebResponse
    {
        private Stream _stream;
        private Uri _uri;
        private string _contentType;
        private WebHeaderCollection _headers;
        private HttpStatusCode _statusCode;

        public ShimWebResponse(Stream stream, Uri uri, string contentType, HttpStatusCode statusCode)
            : base()
        {
            _stream = stream;
            _uri = uri;
            _contentType = contentType;
            _headers = new WebHeaderCollection();
            _statusCode = statusCode;

            _headers.Add("content-type", _contentType);
        }

        public override Stream GetResponseStream()
        {
            return _stream;
        }

        public override Uri ResponseUri
        {
            get
            {
                return _uri;
            }
        }

        public HttpStatusCode StatusCode
        {
            get
            {
                return _statusCode;
            }
        }

        public override long ContentLength
        {
            get
            {
                return _stream.Length;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public override string ContentType
        {
            get
            {
                return _contentType;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public override WebHeaderCollection Headers
        {
            get
            {
                return _headers;
            }
        }

    }
}
