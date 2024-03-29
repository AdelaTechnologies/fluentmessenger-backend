﻿using FluentMessenger.API.Dtos;
using FluentMessenger.API.Entities;
using FluentMessenger.API.Interfaces;
using FluentMessenger.API.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Threading.Tasks;

namespace FluentMessenger.API.Controllers {
    [Route("api/payment")]
    public class PaymentController : Controller {
        private readonly IRepository<User> _userRepo;
        private readonly Secret _appSettings;

        public PaymentController(IRepository<User> repositoryService, IOptions<Secret> appSettings) {
            _userRepo = repositoryService;
            _appSettings = appSettings.Value;
        }

        /// <summary>
        /// Returns the payment page
        /// </summary>
        /// <param name="userId">The user's Id, that wants to make payment</param>
        /// <param name="amount">The amount the user wants to pay</param>
        /// <returns></returns>
        [HttpGet("{UserId}")]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Index(int userId, [FromQuery] decimal amount) {
            var user = _userRepo.Get(userId);
            if (user == null) {
                return NotFound("No user was found");
            }

            ViewData["Title"] = "Payment";
            ViewData["UserId"] = userId;
            ViewData["UserEmail"] = user.Email;
            ViewData["UserAmount"] = amount;
            ViewData["UserFirstName"] = user.OtherNames;
            ViewData["UserLastName"] = user.Surname;
            return View();
        }

        /// <summary>
        /// This confirms the payment by the user and updates the user's smscredit
        /// </summary>
        /// <param name="paymentVerification">The input object for confirming payment</param>
        /// <returns></returns>
        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [Obsolete]
        public async Task<IActionResult> Index([FromBody]
        PaymentVerificationDto paymentVerification) {

            using (var httpClient = new HttpClient()) {
                var url = "https://api.paystack.co/transaction?page=1&perPage=10";
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                    scheme: "Bearer",
                    parameter: _appSettings.PayStackKey
                );
                var response = await httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode) {
                    var transactionDataContext = JsonConvert.
                        DeserializeObject<TransactionDataContext>(
                        await response.Content.ReadAsStringAsync());

                    var transactionData = transactionDataContext.Data
                                          .Find(x => x.Reference == paymentVerification.TransactionReference);
                    if (transactionData == null) {
                        return NotFound($"Invalid transaction ID {paymentVerification.TransactionReference}");
                    }

                    var user = _userRepo.Get(paymentVerification.UserId);
                    if (user == null) {
                        return NotFound("No user was found");
                    }

                    if (user.Email == paymentVerification.Email
                        && transactionData.Status == "success"
                        /*&& transactionData.Domain!= "test"*/) {
                        var numberOfUnits = transactionData.Amount / CostPerUnit;
                        user.SMSCredit += numberOfUnits.Value;
                        _userRepo.Update(user);
                        _userRepo.SaveChanges();
                    }
                }
            }
            return View();
        }

        /// <summary>
        /// This confirms the payment by the user and updates the user's smscredit
        /// </summary>
        /// <param name="body">The input object for confirming payment</param>
        /// <returns></returns>
        [HttpPost("confirm")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult ConfirmPaymentFromWebhooks([FromBody] object body) {
            // Verify event -- You should implement this as soon as possible
            //var ip = HttpContext.Connection.RemoteIpAddress.MapToIPv4().ToString();
            //Console.WriteLine("Ip = " + ip);
            //if(ip != "52.31.139.75" && ip!= "52.49.173.169" && ip != "52.214.14.220") {
            //    Console.WriteLine("Not from paystack");
            //    return Ok();
            //}
            if (body == null || Request.Headers["X-Paystack-Signature"].ToString() == null) {
                Console.WriteLine("Wrong Request");
                return Ok();
            }

            var webhooksVerification = JsonConvert.DeserializeObject<WebhooksVerificationDto>(body.ToString());

            if (webhooksVerification.Event != "charge.success") {
                Console.WriteLine("Wrong Event");
                return Ok();
            }

            var data = webhooksVerification.Data;

            // Verify user
            var customer = data.Customer;
            var sequence = $"{customer.Email}{customer.Last_Name}{customer.First_Name}";
            var user = _userRepo.GetAll(true).FirstOrDefault(x =>
                          x.Email + x.Surname + x.OtherNames == sequence
                          || x.Email + x.OtherNames + x.Surname == sequence);

            if (user == null) {
                Console.WriteLine("Customer not found");
                return Ok("No user was found");
            }


            // offer service
            if (data.Status == "success") {
                var numberOfUnits = data.Amount / CostPerUnit;
                user.SMSCredit += numberOfUnits;
                var notifications = user.Notifications != null ?
                    user.Notifications.ToList() : new List<Notification>();
                notifications.Add(new Notification {
                    Text = $"Your {numberOfUnits} SMS units top up was successful.",
                    User = user,
                    Time = DateTime.Now
                });
                user.Notifications = notifications;
                _userRepo.Update(user);
                _userRepo.SaveChanges();
                Console.WriteLine("Service offered");
                SendReceipt(user, data, customer);
            }
            return Ok();
        }

        private void SendReceipt(User user, Data data, Customer customer) {
            var to = user.Email;
            var from = _appSettings.Email;
            var password = _appSettings.Password;
            Console.WriteLine(password);
            Console.WriteLine(from);
            var amount = $"NGN {data.Amount / 100}";
            var name = $"{customer.First_Name} {customer.Last_Name}";
            var time = $"{data.Paid_At}";
            var channel = data.Channel;
            var reference = data.Reference;
            var template = System.IO.File.ReadAllText("wwwroot/receipt.html");
            var messageBody = template;
            messageBody = messageBody.Replace("[amount-paid]", amount);
            messageBody = messageBody.Replace("[name-paid]", name);
            messageBody = messageBody.Replace("[time-paid]", time);
            messageBody = messageBody.Replace("[channel-paid]", channel);
            messageBody = messageBody.Replace("[reference-paid]", reference);

            using var mailMessage = new MailMessage(from, to);
            mailMessage.Subject = "Payment Receipt";
            mailMessage.Body = messageBody;
            mailMessage.IsBodyHtml = true;
            var smtp = new SmtpClient {
                Host = "smtp.gmail.com",
                EnableSsl = true
            };
            var networkCredential = new NetworkCredential(from, password);
            smtp.UseDefaultCredentials = false;
            smtp.Port = 587;
            smtp.Credentials = networkCredential;
            smtp.Send(mailMessage);
        }

        private const decimal CostPerUnit = 298;
    }
}
