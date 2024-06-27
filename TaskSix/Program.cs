using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using HtmlBuilders;
using LiteDB;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Scriban;
using System;
using System.Globalization;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using static HtmlBuilders.HtmlTags;


const string DisplayDateFormat = "MMMM dd, yyyy";
const string HomePageName = "home-page";
const string HtmlMime = "text/html";

var builder = WebApplication.CreateBuilder();
builder.Services
  .AddSingleton<Wiki>()
  .AddSingleton<Render>()
  .AddAntiforgery()
  .AddMemoryCache();

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();

app.MapGet("/", async (Wiki wiki, Render render) =>
{
    Page? page = await wiki.GetPageAsync(HomePageName);

    if (page is not object)
        return Results.Redirect($"/{HomePageName}");
    var pageContent = await render.BuildPageAsync(HomePageName, atBody: () =>
    {
        return new[]
        {
            RenderPageContent(page),
            RenderPageAttachments(page),
            A.Href($"/edit?pageName={HomePageName}").Class("uk-button uk-button-default uk-button-small").Append("Edit").ToHtmlString()
        };
    }, atSidePanel: () => AllPagesAsync(wiki));
    return Results.Text( pageContent.ToString(),HtmlMime);

});



app.MapGet("/new-page", async (HttpContext context, Wiki wiki) =>
{
    string? pageName = context.Request.Query["pageName"];

    if (string.IsNullOrEmpty(pageName))
    {
        app.Logger.LogWarning("Invalid empty string to add");
        return Results.Redirect("/");
    }
    string ToKebabCase(string str)
	{
		Regex pattern = new Regex(@"[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+");
		return string.Join("-", pattern.Matches(str)).ToLower();
	}
    string kebabCaseName = ToKebabCase(pageName);
    return Results.Redirect($"/{kebabCaseName}");
});


app.MapGet("/edit", async (string pageName, HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    Page? page = await wiki.GetPageAsync(pageName);

    if (page is null)
    {
        return Results.NotFound();
    }

    var editorPageContent = await render.BuildEditorPageAsync(pageName,
        atBodyAsync: async () =>
        {
            var formContent = BuildForm(new PageInput(page!.Id, pageName, page.Content, null), path: $"{pageName}", antiForgery.GetAndStoreTokens(context));
            var attachmentsContent = RenderPageAttachmentsForEdit(page!, antiForgery.GetAndStoreTokens(context));
            return new[] { formContent, attachmentsContent };
        },
        atSidePanelAsync: async () =>
        {
            var list = new List<string>();

            if (!pageName!.ToString().Equals(HomePageName, StringComparison.Ordinal))
                list.Add(RenderDeletePageButton(page!, antiForgery.GetAndStoreTokens(context)));

            list.Add(Br.ToHtmlString());
            list.AddRange(AllPagesForEditing(wiki));
            return list; 
        }
    );

    return Results.Text(editorPageContent.ToString(), HtmlMime);
});


app.MapGet("/attachment", async (string fileId, Wiki wiki) =>
{
	var file = await wiki.GetFileAsync(fileId);
	if (file is not object)
		return Results.NotFound();

	app!.Logger.LogInformation("Attachment " + file.Value.meta.Id + " - " + file.Value.meta.Filename);

	return Results.File(file.Value.file, file.Value.meta.MimeType);
});



app.MapGet("/{pageName}", async (string pageName, HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    pageName = pageName ?? "";

    Page? page = await wiki.GetPageAsync(pageName);

    if (page is object)
    {
        HtmlString pageContent = await render.BuildPageAsync(pageName, atBody: () =>
            new[]
            {
                RenderPageContent(page),
                RenderPageAttachments(page),
                Div.Class("last-modified").Append("Last modified: " + page.LastModifiedUtc.ToString(DisplayDateFormat)).ToHtmlString(),
                A.Href($"/edit?pageName={pageName}").Append("Edit").ToHtmlString()
            },
            atSidePanel: ()  => AllPagesAsync(wiki)
        );

        return Results.Text(pageContent.ToString(), HtmlMime);
    }
    else
    {
        var editorPageContent = await render.BuildEditorPageAsync(pageName,
             atBodyAsync: async () =>
               new[]
               {
            BuildForm(new PageInput(null, pageName, string.Empty, null), path: pageName, antiForgery: antiForgery.GetAndStoreTokens(context))
               },
             atSidePanelAsync: async () => AllPagesForEditing(wiki));
		return Results.Text(editorPageContent.ToString(),HtmlMime);
    }
});


app.MapPost("/delete-page", async (HttpContext context, IAntiforgery antiForgery, Wiki wiki) =>
{
	await antiForgery.ValidateRequestAsync(context);
	var id = context.Request.Form["Id"];

	if (StringValues.IsNullOrEmpty(id))
	{
		app.Logger.LogWarning($"Unable to delete page because form Id is missing");
		return Results.Redirect("/");
	}

	var (isOk, exception) = wiki.DeletePage(Convert.ToInt32(id), HomePageName);

	if (!isOk && exception is object)
		app.Logger.LogError(exception, $"Error in deleting page id {id}");
	else if (!isOk)
		app.Logger.LogError($"Unable to delete page id {id}");

	return Results.Redirect("/");
});

app.MapPost("/delete-attachment", async (HttpContext context, IAntiforgery antiForgery, Wiki wiki) =>
{
	await antiForgery.ValidateRequestAsync(context);
	var id = context.Request.Form["Id"];

	if (StringValues.IsNullOrEmpty(id))
	{
		app.Logger.LogWarning($"Unable to delete attachment because form Id is missing");
		return Results.Redirect("/");
	}

	var pageId = context.Request.Form["PageId"];
	if (StringValues.IsNullOrEmpty(pageId))
	{
		app.Logger.LogWarning($"Unable to delete attachment because form PageId is missing");
		return Results.Redirect("/");
	}

	var (isOk, page, exception) = wiki.DeleteAttachment(Convert.ToInt32(pageId), id.ToString());

	if (!isOk)
	{
		if (exception is object)
			app.Logger.LogError(exception, $"Error in deleting page attachment id {id}");
		else
			app.Logger.LogError($"Unable to delete page attachment id {id}");

		if (page is object)
			return Results.Redirect($"/{page.Name}");
		else
			return Results.Redirect("/");
	}

	return Results.Redirect($"/{page!.Name}");
});


app.MapPost("/{pageName}", async (HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    var pageName = context.Request.RouteValues["pageName"] as string ?? "";
    await antiForgery.ValidateRequestAsync(context);

    PageInput input = await PageInput.FromAsync(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new PageInputValidator(pageName, HomePageName);
    var validationResult = await validator.ValidateAsync(input); 

    validationResult.AddToModelState(modelState, null);


    if (!modelState.IsValid)
    {
        var editorPageContent = await render.BuildEditorPageAsync(pageName,
            atBodyAsync: async () =>
            {
                var formContent =(input, path: $"{pageName}", antiForgery.GetAndStoreTokens(context), modelState);
                return new List<string> { formContent.ToString() };
            },
            atSidePanelAsync: async ()  =>   AllPagesAsync(wiki)
        );
        return Results.Text(editorPageContent.ToString(), HtmlMime);
    }

    try
    {
        var (isOk, p, ex) = wiki.SavePage(input); 

        if (!isOk)
        {
            app.Logger.LogError(ex, "Problem in saving page");
            return Results.Problem("Problem in saving page");
        }

        return Results.Redirect($"/{p!.Name}");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error saving page");
        return Results.Problem("Error saving page");
    }
});

await app.RunAsync();


static IEnumerable<string> AllPagesAsync(Wiki wiki)
{
    var pages = wiki.ListAllPages(); 

	return new[]
	{
		@"<span class=""uk-label"">Pages</span>",
		@"<ul class=""uk-list"">",
		string.Join("",
			wiki.ListAllPages().OrderBy(x => x.Name)
				 .Select(x => Li.Append(A.Href(x.Name).Append(x.Name)).ToHtmlString()
		)
				 ),
		"</ul>"
    };
}

static string[] AllPagesForEditing(Wiki wiki)
{
	static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

	return new[]
	{
	  @"<span class=""uk-label"">Pages</span>",
	  @"<ul class=""uk-list"">",
	  string.Join("",
		wiki.ListAllPages().OrderBy(x => x.Name)
		  .Select(x => Li.Append(Div.Class("uk-inline")
			  .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
			  .Append(Input.Text.Value($"[{KebabToNormalCase(x.Name)}](/{x.Name})").Class("uk-input uk-form-small").Style("cursor", "pointer").Attribute("onclick", "copyMarkdownLink(this);"))
		  ).ToHtmlString()
		)
	  ),
	  "</ul>"
	};
}

static string RenderMarkdown(string str)
{
	var sanitizer = new HtmlSanitizer();
	return sanitizer.Sanitize(Markdown.ToHtml(str, new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UseAdvancedExtensions().Build()));
}

static string RenderPageContent(Page page) => RenderMarkdown(page.Content);

static string RenderDeletePageButton(Page page, AntiforgeryTokenSet antiForgery)
{
	var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
	HtmlTag id = Input.Hidden.Name("Id").Value(page.Id.ToString());
	var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-danger").Id("deleteButton").Append("Delete"));

	var form = Form
			   .Attribute("method", "post")
			   .Attribute("action", $"/delete-page")
			   .Attribute("onsubmit", $"return confirm('Please confirm to delete this page');")
				 .Append(antiForgeryField)
				 .Append(id)
				 .Append(submit);

	return form.ToHtmlString();
}

static string RenderPageAttachmentsForEdit(Page page, AntiforgeryTokenSet antiForgery)
{
	if (page.Attachments.Count == 0)
		return string.Empty;

	var label = Span.Class("uk-label").Append("Attachments");
	var list = Ul.Class("uk-list");

	HtmlTag CreateEditorHelper(Attachment attachment) =>
	  Span.Class("uk-inline")
		  .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
		  .Append(Input.Text.Value($"[{attachment.FileName}](/attachment?fileId={attachment.FileId})")
			.Class("uk-input uk-form-small uk-form-width-large")
			.Style("cursor", "pointer")
			.Attribute("onclick", "copyMarkdownLink(this);")
		  );

	static HtmlTag CreateDelete(int pageId, string attachmentId, AntiforgeryTokenSet antiForgery)
	{
		var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
		var id = Input.Hidden.Name("Id").Value(attachmentId.ToString());
		var name = Input.Hidden.Name("PageId").Value(pageId.ToString());

		var submit = Button.Class("uk-button uk-button-danger uk-button-small").Append(Span.Attribute("uk-icon", "icon: close; ratio: .75;"));
		var form = Form
			   .Style("display", "inline")
			   .Attribute("method", "post")
			   .Attribute("action", $"/delete-attachment")
			   .Attribute("onsubmit", $"return confirm('Please confirm to delete this attachment');")
				 .Append(antiForgeryField)
				 .Append(id)
				 .Append(name)
				 .Append(submit);

		return form;
	}

	foreach (var attachment in page.Attachments)
	{
		list = list.Append(Li
		  .Append(CreateEditorHelper(attachment))
		  .Append(CreateDelete(page.Id, attachment.FileId, antiForgery))
		);
	}
	return label.ToHtmlString() + list.ToHtmlString();
}

static string RenderPageAttachments(Page page)
{
	if (page.Attachments.Count == 0)
		return string.Empty;

	var label = Span.Class("uk-label").Append("Attachments");
	var list = Ul.Class("uk-list uk-list-disc");
	foreach (var attachment in page.Attachments)
	{
		list = list.Append(Li.Append(A.Href($"/attachment?fileId={attachment.FileId}").Append(attachment.FileName)));
	}
	return label.ToHtmlString() + list.ToHtmlString();
}

static string BuildForm(PageInput input, string path, AntiforgeryTokenSet antiForgery, ModelStateDictionary? modelState = null)
{
	bool IsFieldOK(string key) => modelState!.ContainsKey(key) && modelState[key]!.ValidationState == ModelValidationState.Invalid;

	var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

	var nameField = Div
	  .Append(HtmlTags.Label.Class("uk-form-label").Append(nameof(input.Name)))
	  .Append(Div.Class("uk-form-controls")
		.Append(Input.Text.Class("uk-input").Name("Name").Value(input.Name))
	  );

	var contentField = Div
	  .Append(HtmlTags.Label.Class("uk-form-label").Append(nameof(input.Content)))
	  .Append(Div.Class("uk-form-controls")
		.Append(Textarea.Name("Content").Class("uk-textarea").Append(input.Content))
	  );

	var attachmentField = Div
	  .Append(HtmlTags.Label.Class("uk-form-label").Append(nameof(input.Attachment)))
	  .Append(Div.Attribute("uk-form-custom", "target: true")
		.Append(Input.File.Name("Attachment"))
		.Append(Input.Text.Class("uk-input uk-form-width-large").Attribute("placeholder", "Click to select file").ToggleAttribute("disabled", true))
	  );

	if (modelState is object && !modelState.IsValid)
	{
		if (IsFieldOK("Name"))
		{
			foreach (var er in modelState["Name"]!.Errors)
			{
				nameField = nameField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
			}
		}

		if (IsFieldOK("Content"))
		{
			foreach (var er in modelState["Content"]!.Errors)
			{
				contentField = contentField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
			}
		}
	}

	var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Submit"));

	var form = Form
			   .Class("uk-form-stacked")
			   .Attribute("method", "post")
			   .Attribute("enctype", "multipart/form-data")
			   .Attribute("action", $"/{path}")
				 .Append(antiForgeryField)
				 .Append(nameField)
				 .Append(contentField)
				 .Append(attachmentField);

	if (input.Id is object)
	{
		HtmlTag id = Input.Hidden.Name("Id").Value(input.Id.ToString()!);
		form = form.Append(id);
	}

	form = form.Append(submit);

	return form.ToHtmlString();
}


class Render
{
	static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

	static string[] MarkdownEditorHead() => new[]
	{
	  @"<link rel=""stylesheet"" href=""https://unpkg.com/easymde/dist/easymde.min.css"">",
	  @"<script src=""https://unpkg.com/easymde/dist/easymde.min.js""></script>"
	};

	static string[] MarkdownEditorFoot() => new[]
	{
	  @"<script>
        var easyMDE = new EasyMDE({
          insertTexts: {
            link: [""["", ""]()""]
          }
        });

        function copyMarkdownLink(element) {
          element.select();
          document.execCommand(""copy"");
        }
        </script>"
	};

	(Template head, Template body, Template layout) _templates = (
	  head: Scriban.Template.Parse(
		"""
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{ title }}</title>
        <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/css/uikit.min.css" />
        {{ header }}
        <style>
        html{
          background-color: #1e1e1e !important;
        }
          body {
            background-color: #1e1e1e !important;
            color: #ffffff;
            height : 100%;
          }
          .uk-navbar-container, .uk-container, .uk-card, .uk-section {
            background-color: #1e1e1e !important ; 
            color: #ffffff; 
          } .uk-label{background-color:#3a3a3a; width:90px; text-align:center; border-style: solid; border-width: 1px;}
          .last-modified { font-size: small; }
          a:visited { color: lightblue; }
          .uk-button {
            background-color: #2d2d2d; 
            color: #ffffff; 
          }
          .uk-button-default {
            background-color: #3a3a3a; 
            color: #ffffff; border-radius:2px;
          }
          .uk-button-primary {
            background-color: #007bff;  border-color: white; text-align:center;
            color: #ffffff; width : 110px; height:40px; border-radius:2px;
          }
          #deleteButton {
            background-color: #dc3545;  border-color: white; text-align:center;
            color: #ffffff;  width : 110px; height:40px;  border-radius:2px;
          }uk-form-label{color:grey;}
          input.uk-input, textarea.uk-textarea, input.uk-form-width-large, input.uk-form-small {
            background-color: #1e1e1e; 
            color: #ffffff; 
            border: 1px solid #3a3a3a; 
          } .editor-toolbar {background-color: grey;}
          .uk-form-danger {
            color: #ff4d4d;
          } .uk-width-1-5{text-align: center;}
        </style>
        """
	  ),
	  body: Scriban.Template.Parse(
		"""
        <nav class="uk-navbar-container">
          <div class="uk-container">
            <div class="uk-navbar">
              <div class="uk-navbar-left">
                <ul class="uk-navbar-nav">
                  <li class="uk-active"><a href="/"><span uk-icon="home" style="color:white;width:30px;"></span></a></li>
                </ul>
              </div>
              <div class="uk-navbar-center">
                <div class="uk-navbar-item">
                  <form action="/new-page">
                    <input class="uk-input uk-form-width-large" type="text" name="pageName" placeholder="Type desired page title here"></input>
                    <input type="submit"  class="uk-button uk-button-default" value="Add New Page">
                  </form>
                </div>
              </div>
            </div>
          </div>
        </nav>
        {{ if at_side_panel != "" }}
          <div class="uk-container">
          <div uk-grid>
            <div class="uk-width-4-5">
              <h1 style="color:grey">{{ page_name }}</h1>
              {{ content }}
            </div>
            <div class="uk-width-1-5">
              {{ at_side_panel }}
            </div>
          </div>
          </div>
        {{ else }}
          <div class="uk-container">
            <h1>{{ page_name }}</h1>
            {{ content }}
          </div>
        {{ end }}

        <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
        <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>
        {{ at_foot }}
        """
	  ),
	  layout: Scriban.Template.Parse(
		"""
        <!DOCTYPE html>
        <head>
          {{ head }}
        </head>
        <body>
          {{ body }}
        </body>
        </html>
        """
	  )
	);

    public async Task<HtmlString> BuildEditorPageAsync(string title, Func<Task<IEnumerable<string>>>? atBodyAsync, Func<Task<IEnumerable<string>>>? atSidePanelAsync = null)
    {
        Func<IEnumerable<string>>? atBody = null;
        Func<IEnumerable<string>>? atSidePanel = null;

        if (atBodyAsync != null)
        {
            atBody = () => atBodyAsync().Result; 
        }

        if (atSidePanelAsync != null)
        {
            atSidePanel = () => atSidePanelAsync().Result; 
        }

        return await BuildPageAsync(
            title,
            atHead: () => MarkdownEditorHead(),
            atBody: atBody,
            atSidePanel: atSidePanel,
            atFoot: () => MarkdownEditorFoot()
        );
    }

    public async Task<HtmlString> BuildPageAsync(string title, Func<IEnumerable<string>>? atHead = null, Func<IEnumerable<string>>? atBody = null, Func<IEnumerable<string>>? atSidePanel = null, Func<IEnumerable<string>>? atFoot = null)
    {
        var head = _templates.head.Render(new
        {
            title,
            header = string.Join("\r", atHead?.Invoke() ?? new[] { "" })
        });

        var body = _templates.body.Render(new
        {
            PageName = KebabToNormalCase(title),
            Content = string.Join("\r", atBody?.Invoke() ?? new[] { "" }),
            AtSidePanel = string.Join("\r", atSidePanel?.Invoke() ?? new[] { "" }),
            AtFoot = string.Join("\r", atFoot?.Invoke() ?? new[] { "" })
        });

        return new HtmlString(_templates.layout.Render(new { head, body }));
    }

}

class Wiki
{
	DateTime Timestamp() => DateTime.UtcNow;

	const string PageCollectionName = "Pages";
	const string AllPagesKey = "AllPages";
	const double CacheAllPagesForMinutes = 30;

	readonly IWebHostEnvironment _env;
	readonly IMemoryCache _cache;
	readonly ILogger _logger;

	public Wiki(IWebHostEnvironment env, IMemoryCache cache, ILogger<Wiki> logger)
	{
		_env = env;
		_cache = cache;
		_logger = logger;
	}

	string GetDbPath() => Path.Combine(_env.ContentRootPath, "wiki.db");

	public List<Page> ListAllPages()
	{
		var pages = _cache.Get(AllPagesKey) as List<Page>;

		if (pages is object)
			return pages;

		using var db = new LiteDatabase(GetDbPath());
		var coll = db.GetCollection<Page>(PageCollectionName);
		var items = coll.Query().ToList();

		_cache.Set(AllPagesKey, items, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheAllPagesForMinutes)));
		return items;
	}

    public Task<Page?> GetPageAsync(string path)
    {
        return Task.Run(() =>
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            coll.EnsureIndex(x => x.Name);

            var page = coll.Query()
                           .Where(x => x.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
                           .FirstOrDefault();

            return page ?? null;
        });
    }



    public (bool isOk, Page? page, Exception? ex) SavePage(PageInput input)
	{
		try
		{
			using var db = new LiteDatabase(GetDbPath());
			var coll = db.GetCollection<Page>(PageCollectionName);
			coll.EnsureIndex(x => x.Name);

			Page? existingPage = input.Id.HasValue ? coll.FindOne(x => x.Id == input.Id) : null;

			var sanitizer = new HtmlSanitizer();
			var properName = input.Name.ToString().Trim().Replace(' ', '-').ToLower();

			Attachment? attachment = null;
			if (!string.IsNullOrWhiteSpace(input.Attachment?.FileName))
			{
				attachment = new Attachment
				(
					FileId: Guid.NewGuid().ToString(),
					FileName: input.Attachment.FileName,
					MimeType: input.Attachment.ContentType,
					LastModifiedUtc: Timestamp()
				);

				using var stream = input.Attachment.OpenReadStream();
				var res = db.FileStorage.Upload(attachment.FileId, input.Attachment.FileName, stream);
			}

			if (existingPage is not object)
			{
				var newPage = new Page
				{
					Name = sanitizer.Sanitize(properName),
					Content = input.Content, 
					LastModifiedUtc = Timestamp()
				};

				if (attachment is object)
					newPage.Attachments.Add(attachment);

				coll.Insert(newPage);

				_cache.Remove(AllPagesKey);
				return (true, newPage, null);
			}
			else
			{
				var updatedPage = existingPage with
				{
					Name = sanitizer.Sanitize(properName),
					Content = input.Content,
					LastModifiedUtc = Timestamp()
				};

				if (attachment is object)
					updatedPage.Attachments.Add(attachment);

				coll.Update(updatedPage);

				_cache.Remove(AllPagesKey);
				return (true, updatedPage, null);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, $"There is an exception in trying to save page name '{input.Name}'");
			return (false, null, ex);
		}
	}

	public (bool isOk, Page? p, Exception? ex) DeleteAttachment(int pageId, string id)
	{
		try
		{
			using var db = new LiteDatabase(GetDbPath());
			var coll = db.GetCollection<Page>(PageCollectionName);
			var page = coll.FindById(pageId);
			if (page is not object)
			{
				_logger.LogWarning($"Delete attachment operation fails because page id {id} cannot be found in the database");
				return (false, null, null);
			}

			if (!db.FileStorage.Delete(id))
			{
				_logger.LogWarning($"We cannot delete this file attachment id {id} and it's a mystery why");
				return (false, page, null);
			}

			page.Attachments.RemoveAll(x => x.FileId.Equals(id, StringComparison.OrdinalIgnoreCase));

			var updateResult = coll.Update(page);

			if (!updateResult)
			{
				_logger.LogWarning($"Delete attachment works but updating the page (id {pageId}) attachment list fails");
				return (false, page, null);
			}

			return (true, page, null);
		}
		catch (Exception ex)
		{
			return (false, null, ex);
		}
	}

	public (bool isOk, Exception? ex) DeletePage(int id, string homePageName)
	{
		try
		{
			using var db = new LiteDatabase(GetDbPath());
			var coll = db.GetCollection<Page>(PageCollectionName);

			var page = coll.FindById(id);

			if (page is not object)
			{
				_logger.LogWarning($"Delete operation fails because page id {id} cannot be found in the database");
				return (false, null);
			}

			if (page.Name.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogWarning($"Page id {id}  is a home page and elete operation on home page is not allowed");
				return (false, null);
			}

			foreach (var a in page.Attachments)
			{
				db.FileStorage.Delete(a.FileId);
			}

			if (coll.Delete(id))
			{
				_cache.Remove(AllPagesKey);
				return (true, null);
			}

			_logger.LogWarning($"Somehow we cannot delete page id {id} and it's a mistery why.");
			return (false, null);
		}
		catch (Exception ex)
		{
			return (false, ex);
		}
	}

 
    public async Task<(LiteFileInfo<string> meta, byte[] file)?> GetFileAsync(string fileId)
    {
        using var db = new LiteDatabase(GetDbPath());

        var meta = db.FileStorage.FindById(fileId);
        if (meta is not object)
            return null;

        using var stream = new MemoryStream();
        await Task.Run(() => db.FileStorage.Download(fileId, stream));
        return (meta, stream.ToArray());
    }
}

record Page
{
	public int Id { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Content { get; set; } = string.Empty;

	public DateTime LastModifiedUtc { get; set; }

	public List<Attachment> Attachments { get; set; } = new();
}

record Attachment
(
	string FileId,

	string FileName,

	string MimeType,

	DateTime LastModifiedUtc
);

record PageInput(int? Id, string Name, string Content, IFormFile? Attachment)
{
    public static async Task<PageInput> FromAsync(IFormCollection form)
    {
        var (id, name, content) = (form["Id"], form["Name"], form["Content"]);

        int? pageId = null;

        if (!StringValues.IsNullOrEmpty(id))
            pageId = Convert.ToInt32(id);

        IFormFile? file = form.Files["Attachment"];

        return new PageInput(pageId, name!, content!, file);
    }
}

class PageInputValidator : AbstractValidator<PageInput>
{
    public PageInputValidator(string pageName, string homePageName)
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");

        if (pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            RuleFor(x => x.Name).Must(name => name.Equals(homePageName)).WithMessage($"You cannot modify home page name. Please keep it {homePageName}");

        RuleFor(x => x.Content).NotEmpty().WithMessage("Content is required");
    }
}
