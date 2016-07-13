using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Web.Http;
using System.Web.Http.Controllers;
using Newtonsoft.Json;
using System.Configuration;

namespace DevTeam.ModelActionSelector
{
    internal class UnmathedController
    {
        public Type ControllerType { get; set; }
        public List<UnmathedActionDescriptor> UnmatchedActions { get; set; }
    }

    internal class UnmathedActionDescriptor
    {
        public MethodInfo Action { get; set; }
        public string Verb { get; set; }
        public List<string> Parameters { get; set; }
    }

    public class ModelActionSelector : ApiControllerActionSelector
    {
        private List<UnmathedController> _unmathedControllers;

        public ModelActionSelector()
        {
            Initialize();
        }

        private void Initialize()
        {
            var assemblyName = ConfigurationManager.AppSettings["assemblyName"];

            if (assemblyName == null)
                throw new ConfigurationErrorsException("You must define assemblyName in your appSettings");

            _unmathedControllers = new List<UnmathedController>();

            try
            {
                var controllers =
                    Assembly.Load(assemblyName)
                     .GetTypes()
                     .Where(t => typeof(ApiController).IsAssignableFrom(t))
                     .Select(t => new UnmathedController
                     {
                         ControllerType = t,
                         UnmatchedActions = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                             .Where(m => !m.Name.ToLower().StartsWith("get")
                                                      && !m.GetCustomAttributes<HttpGetAttribute>().Any())
                                             .Select(m => new
                                             {
                                                 Action = m,
                                                 Verb = GetMethodType(m.Name)
                                             })
                                             .GroupBy(m => m.Verb)
                                             .Where(g => g.Count() > 1)
                                             .SelectMany(g => g
                                                 .Select(m => new UnmathedActionDescriptor
                                                 {
                                                     Action = m.Action,
                                                     Verb = m.Verb.ToUpper(),
                                                     Parameters = m.Action.GetParameters()
                                                                          .Single(p => p.ParameterType.IsClass
                                                                                    && !p.GetCustomAttributes(typeof(FromUriAttribute)).Any())
                                                                          .ParameterType
                                                                          .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                                          .Where(p => p.GetCustomAttribute<ClientIgnoreAttribute>() == null)
                                                                          .Select(p => p.Name.ToLower())
                                                                          .ToList()
                                                 }))
                                             .ToList()
                     });

                _unmathedControllers = controllers.Where(c => c.UnmatchedActions.Any()).ToList();
            }
            catch (InvalidOperationException e)
            {
                throw new Exception("ModelActionSelector found more then one complex type in one action", e);
            }
        }

        private string GetMethodType(string methodName)
        {
            var index = 1;
            while (index < methodName.Length && !char.IsUpper(methodName[index]))
                index++;
            return methodName.Substring(0, index);
        }

        public override HttpActionDescriptor SelectAction(HttpControllerContext controllerContext)
        {
            var unmatchedController = _unmathedControllers
                .SingleOrDefault(a => a.ControllerType == controllerContext.ControllerDescriptor.ControllerType);

            var method = controllerContext.Request.Method.Method;

            if (unmatchedController == null
             || unmatchedController.UnmatchedActions.All(a => a.Verb != method))
                return base.SelectAction(controllerContext);

            var bodyData = GetBodyData(controllerContext);
            var targetActions = unmatchedController.UnmatchedActions.Where(a => a.Verb == method).ToList();

            var actions = from action in targetActions
                          where action.Parameters.Count <= bodyData.Count
                          select new
                          {
                              Action = action.Action,
                              Percent = action.Parameters.Intersect(bodyData).Count() / (decimal)bodyData.Count
                          };

            actions = actions.ToList();

            if (!actions.Any())
                throw new ArgumentException("Can't find valid action for granted argumets.");

            var max = actions.Max(a => a.Percent);
            var betterActions = actions.Where(a => a.Percent == max).ToList();

            if (betterActions.Count > 1)
                throw new HttpResponseException(HttpStatusCode.MultipleChoices);

            return new ReflectedHttpActionDescriptor(controllerContext.ControllerDescriptor, betterActions.Single().Action);
        }

        private List<string> GetBodyData(HttpControllerContext controllerContext)
        {
            var requestContent = new HttpMessageContent(controllerContext.Request);
            var json = requestContent.HttpRequestMessage.Content.ReadAsStringAsync().Result;
            var bodyData = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            return bodyData != null ? bodyData.Select(d => d.Key.ToLower()).ToList() : new List<string>();
        }
    }
}
